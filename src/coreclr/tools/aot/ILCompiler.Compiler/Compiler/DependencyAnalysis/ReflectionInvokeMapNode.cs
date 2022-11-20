// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using Internal.Text;
using Internal.TypeSystem;
using Internal.NativeFormat;

using Debug = System.Diagnostics.Debug;
using InvokeTableFlags = Internal.Runtime.InvokeTableFlags;

namespace ILCompiler.DependencyAnalysis
{
    /// <summary>
    /// Represents a map between reflection metadata and generated method bodies.
    /// </summary>
    internal sealed class ReflectionInvokeMapNode : ObjectNode, ISymbolDefinitionNode
    {
        private ObjectAndOffsetSymbolNode _endSymbol;
        private ExternalReferencesTableNode _externalReferences;

        public ReflectionInvokeMapNode(ExternalReferencesTableNode externalReferences)
        {
            _endSymbol = new ObjectAndOffsetSymbolNode(this, 0, "__method_to_entrypoint_map_End", true);
            _externalReferences = externalReferences;
        }

        public ISymbolNode EndSymbol
        {
            get
            {
                return _endSymbol;
            }
        }

        public void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append(nameMangler.CompilationUnitPrefix).Append("__method_to_entrypoint_map");
        }
        public int Offset => 0;
        public override bool IsShareable => false;

        public override ObjectNodeSection GetSection(NodeFactory factory) => _externalReferences.GetSection(factory);

        public override bool StaticDependenciesAreComputed => true;

        protected override string GetName(NodeFactory factory) => this.GetMangledName(factory.NameMangler);

        public static void AddDependenciesDueToReflectability(ref DependencyList dependencies, NodeFactory factory, MethodDesc method)
        {
            Debug.Assert(factory.MetadataManager.IsReflectionInvokable(method));

            dependencies ??= new DependencyList();

            dependencies.Add(factory.MaximallyConstructableType(method.OwningType), "Reflection invoke");

            if (factory.MetadataManager.HasReflectionInvokeStubForInvokableMethod(method))
            {
                MethodDesc invokeStub = factory.MetadataManager.GetReflectionInvokeStub(method);
                dependencies.Add(factory.MethodEntrypoint(invokeStub), "Reflection invoke");

                var signature = method.Signature;
                AddSignatureDependency(ref dependencies, factory, signature.ReturnType);
                foreach (var parameterType in signature)
                    AddSignatureDependency(ref dependencies, factory, parameterType);

                static void AddSignatureDependency(ref DependencyList dependencies, NodeFactory factory, TypeDesc type)
                {
                    if (type.IsByRef)
                        type = ((ParameterizedType)type).ParameterType;

                    // Pointer runtime type handles can be created at runtime if necessary
                    while (type.IsPointer)
                        type = ((ParameterizedType)type).ParameterType;

                    // Skip tracking dependencies for primitive types. Assume that they are always present.
                    if (type.IsPrimitive || type.IsVoid)
                        return;

                    // Function pointers are not supported yet.
                    // https://github.com/dotnet/runtime/issues/71883
                    if (type.IsFunctionPointer)
                        return;

                    TypeDesc canonType = type.ConvertToCanonForm(CanonicalFormKind.Specific);
                    if (canonType.IsCanonicalSubtype(CanonicalFormKind.Any))
                        GenericTypesTemplateMap.GetTemplateTypeDependencies(ref dependencies, factory, type.ConvertToCanonForm(CanonicalFormKind.Specific));
                    else
                        dependencies.Add(factory.MaximallyConstructableType(canonType), "Reflection invoke");
                }
            }

            if (method.OwningType.IsValueType && !method.Signature.IsStatic)
                dependencies.Add(factory.ExactCallableAddress(method, isUnboxingStub: true), "Reflection unboxing stub");

            // If the method is defined in a different module than this one, a metadata token isn't known for performing the reference
            // Use a name/sig reference instead.
            if (!factory.MetadataManager.WillUseMetadataTokenToReferenceMethod(method))
            {
                dependencies.Add(factory.NativeLayout.PlacedSignatureVertex(factory.NativeLayout.MethodNameAndSignatureVertex(method.GetTypicalMethodDefinition())),
                    "Non metadata-local method reference");
            }

            if (method.HasInstantiation)
            {
                if (method.IsCanonicalMethod(CanonicalFormKind.Universal))
                {
                    dependencies.Add(factory.NativeLayout.PlacedSignatureVertex(factory.NativeLayout.MethodNameAndSignatureVertex(method)),
                        "UniversalCanon signature of method");
                }
                else if (!method.GetCanonMethodTarget(CanonicalFormKind.Specific).RequiresInstArg() || method.IsAbstract)
                {
                    foreach (var instArg in method.Instantiation)
                    {
                        dependencies.Add(factory.NecessaryTypeSymbol(instArg), "Reflectable generic method inst arg");
                    }
                }
            }

            ReflectionVirtualInvokeMapNode.GetVirtualInvokeMapDependencies(ref dependencies, factory, method);
        }

        public override ObjectData GetData(NodeFactory factory, bool relocsOnly = false)
        {
            // This node does not trigger generation of other nodes.
            if (relocsOnly)
                return new ObjectData(Array.Empty<byte>(), Array.Empty<Relocation>(), 1, new ISymbolDefinitionNode[] { this });

            // Ensure the native layout blob has been saved
            factory.MetadataManager.NativeLayoutInfo.SaveNativeLayoutInfoWriter(factory);

            var writer = new NativeWriter();
            var typeMapHashTable = new VertexHashtable();

            Section hashTableSection = writer.NewSection();
            hashTableSection.Place(typeMapHashTable);

            // Get a list of all methods that have a method body and metadata from the metadata manager.
            foreach (var mappingEntry in factory.MetadataManager.GetMethodMapping(factory))
            {
                MethodDesc method = mappingEntry.Entity;

                if (!factory.MetadataManager.ShouldMethodBeInInvokeMap(method))
                    continue;

                InvokeTableFlags flags = 0;

                if (method.HasInstantiation)
                    flags |= InvokeTableFlags.IsGenericMethod;

                if (method.GetCanonMethodTarget(CanonicalFormKind.Specific).RequiresInstArg())
                {
                    bool goesThroughInstantiatingUnboxingThunk = method.OwningType.IsValueType && !method.Signature.IsStatic && !method.HasInstantiation;
                    if (!goesThroughInstantiatingUnboxingThunk)
                        flags |= InvokeTableFlags.RequiresInstArg;
                }

                if (method.IsDefaultConstructor)
                    flags |= InvokeTableFlags.IsDefaultConstructor;

                if (ReflectionVirtualInvokeMapNode.NeedsVirtualInvokeInfo(method))
                    flags |= InvokeTableFlags.HasVirtualInvoke;

                if (!method.IsAbstract)
                    flags |= InvokeTableFlags.HasEntrypoint;

                if (mappingEntry.MetadataHandle != 0)
                    flags |= InvokeTableFlags.HasMetadataHandle;

                if (!factory.MetadataManager.HasReflectionInvokeStubForInvokableMethod(method))
                    flags |= InvokeTableFlags.NeedsParameterInterpretation;

                if (method.IsCanonicalMethod(CanonicalFormKind.Universal))
                    flags |= InvokeTableFlags.IsUniversalCanonicalEntry;

                // TODO: native signature for P/Invokes and UnmanagedCallersOnly methods
                if (method.IsRawPInvoke() || method.IsUnmanagedCallersOnly)
                    continue;

                // Grammar of an entry in the hash table:
                // Flags + DeclaringType + MetadataHandle/NameAndSig + Entrypoint + DynamicInvokeMethod + [NumGenericArgs + GenericArgs]

                Vertex vertex = writer.GetUnsignedConstant((uint)flags);

                if ((flags & InvokeTableFlags.HasMetadataHandle) != 0)
                {
                    // Only store the offset portion of the metadata handle to get better integer compression
                    vertex = writer.GetTuple(vertex,
                        writer.GetUnsignedConstant((uint)(mappingEntry.MetadataHandle & MetadataManager.MetadataOffsetMask)));
                }
                else
                {
                    var nameAndSig = factory.NativeLayout.PlacedSignatureVertex(factory.NativeLayout.MethodNameAndSignatureVertex(method.GetTypicalMethodDefinition()));
                    vertex = writer.GetTuple(vertex, writer.GetUnsignedConstant((uint)nameAndSig.SavedVertex.VertexOffset));
                }

                // Go with a necessary type symbol. It will be upgraded to a constructed one if a constructed was emitted.
                IEETypeNode owningTypeSymbol = factory.NecessaryTypeSymbol(method.OwningType);
                vertex = writer.GetTuple(vertex,
                    writer.GetUnsignedConstant(_externalReferences.GetIndex(owningTypeSymbol)));

                if ((flags & InvokeTableFlags.HasEntrypoint) != 0)
                {
                    vertex = writer.GetTuple(vertex,
                        writer.GetUnsignedConstant(_externalReferences.GetIndex(
                            factory.MethodEntrypoint(method.GetCanonMethodTarget(CanonicalFormKind.Specific),
                            unboxingStub: method.OwningType.IsValueType && !method.Signature.IsStatic))));
                }

                if ((flags & InvokeTableFlags.NeedsParameterInterpretation) == 0)
                {
                    MethodDesc invokeStub = factory.MetadataManager.GetReflectionInvokeStub(method);
                    vertex = writer.GetTuple(vertex,
                        writer.GetUnsignedConstant(_externalReferences.GetIndex(factory.MethodEntrypoint(invokeStub))));
                }

                if ((flags & InvokeTableFlags.IsGenericMethod) != 0)
                {
                    if ((flags & InvokeTableFlags.IsUniversalCanonicalEntry) != 0)
                    {
                        var nameAndSigGenericMethod = factory.NativeLayout.PlacedSignatureVertex(factory.NativeLayout.MethodNameAndSignatureVertex(method));
                        vertex = writer.GetTuple(vertex, writer.GetUnsignedConstant((uint)nameAndSigGenericMethod.SavedVertex.VertexOffset));
                    }
                    else if ((flags & InvokeTableFlags.RequiresInstArg) == 0 || (flags & InvokeTableFlags.HasEntrypoint) == 0)
                    {
                        VertexSequence args = new VertexSequence();
                        for (int i = 0; i < method.Instantiation.Length; i++)
                        {
                            uint argId = _externalReferences.GetIndex(factory.NecessaryTypeSymbol(method.Instantiation[i]));
                            args.Append(writer.GetUnsignedConstant(argId));
                        }
                        vertex = writer.GetTuple(vertex, args);
                    }
                    else
                    {
                        uint dictionaryId = _externalReferences.GetIndex(factory.MethodGenericDictionary(method));
                        vertex = writer.GetTuple(vertex, writer.GetUnsignedConstant(dictionaryId));
                    }
                }

                int hashCode = method.GetCanonMethodTarget(CanonicalFormKind.Specific).OwningType.GetHashCode();
                typeMapHashTable.Append((uint)hashCode, hashTableSection.Place(vertex));
            }

            byte[] hashTableBytes = writer.Save();

            _endSymbol.SetSymbolOffset(hashTableBytes.Length);

            return new ObjectData(hashTableBytes, Array.Empty<Relocation>(), 1, new ISymbolDefinitionNode[] { this, _endSymbol });
        }

        protected internal override int Phase => (int)ObjectNodePhase.Ordered;
        public override int ClassCode => (int)ObjectNodeOrder.ReflectionInvokeMapNode;
    }
}
