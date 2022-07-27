// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Extension methods for adding configuration related options services to the DI container via <see cref="OptionsBuilder{TOptions}"/>.
    /// </summary>
    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2091:UnrecognizedReflectionPattern",
        Justification = "Workaround for https://github.com/mono/linker/issues/1416. Outer method has been annotated with DynamicallyAccessedMembers.")]
    public static class OptionsBuilderExtensions
    {
        /// <summary>
        /// Enforces options validation check on start rather than in runtime.
        /// </summary>
        /// <typeparam name="TOptions">The type of options.</typeparam>
        /// <param name="optionsBuilder">The <see cref="OptionsBuilder{TOptions}"/> to configure options instance.</param>
        /// <returns>The <see cref="OptionsBuilder{TOptions}"/> so that additional calls can be chained.</returns>
        public static OptionsBuilder<TOptions> ValidateOnStart<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(this OptionsBuilder<TOptions> optionsBuilder)
            where TOptions : class
        {
            ThrowHelper.ThrowIfNull(optionsBuilder);

            optionsBuilder.Services.AddHostedService<ValidationHostedService>();
            OptionsBuilder<ValidatorOptions> validatorOptionsBuilder = optionsBuilder.Services.AddOptions<ValidatorOptions>();

            if (optionsBuilder.Name == Options.Options.DefaultName)
            {
                validatorOptionsBuilder.Configure<IOptions<TOptions>>((vo, options) =>
                {
                    // This adds an action that resolves the options value to force evaluation
                    // We don't care about the result as duplicates are not important
                    vo.Validators[(typeof(TOptions), Options.Options.DefaultName)] = () => _ = options.Value;
                });
            }
            else
            {
                validatorOptionsBuilder.Configure<IOptionsMonitor<TOptions>>((vo, optionsMonitor) =>
                {
                    vo.Validators[(typeof(TOptions), optionsBuilder.Name)] = () => optionsMonitor.Get(optionsBuilder.Name);
                });
            }

            return optionsBuilder;
        }
    }
}
