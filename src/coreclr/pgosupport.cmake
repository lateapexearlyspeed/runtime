include(CheckCXXSourceCompiles)
include(CheckCXXCompilerFlag)

# VC++ guarantees support for LTCG (LTO's equivalent)
if(NOT WIN32)
  # Function required to give CMAKE_REQUIRED_* local scope
  function(check_have_lto)
    set(CMAKE_REQUIRED_FLAGS -flto)
    set(CMAKE_REQUIRED_LIBRARIES -flto -fuse-ld=gold)
    check_cxx_source_compiles("int main() { return 0; }" HAVE_LTO)
  endfunction(check_have_lto)
  check_have_lto()

  check_cxx_compiler_flag(-faligned-new COMPILER_SUPPORTS_F_ALIGNED_NEW)
endif(NOT WIN32)

# Adds Profile Guided Optimization (PGO) flags to the current target
function(add_pgo TargetName)
    if(CLR_CMAKE_PGO_INSTRUMENT)
        if(CLR_CMAKE_HOST_WIN32)
            set_property(TARGET ${TargetName} APPEND_STRING PROPERTY LINK_FLAGS_RELEASE        " /LTCG /GENPROFILE")
            set_property(TARGET ${TargetName} APPEND_STRING PROPERTY LINK_FLAGS_RELWITHDEBINFO " /LTCG /GENPROFILE")

            if (CLR_CMAKE_HOST_ARCH_AMD64)
                # The /guard:ehcont and /CETCOMPAT switches here are temporary and will be moved to a more global location once
                # new profile data is published
                set_property(TARGET ${TargetName} APPEND_STRING PROPERTY LINK_FLAGS_RELEASE        " /guard:ehcont /CETCOMPAT")
                set_property(TARGET ${TargetName} APPEND_STRING PROPERTY LINK_FLAGS_RELWITHDEBINFO " /guard:ehcont /CETCOMPAT")
            endif (CLR_CMAKE_HOST_ARCH_AMD64)
        else(CLR_CMAKE_HOST_WIN32)
            if(UPPERCASE_CMAKE_BUILD_TYPE STREQUAL RELEASE OR UPPERCASE_CMAKE_BUILD_TYPE STREQUAL RELWITHDEBINFO)
                target_compile_options(${TargetName} PRIVATE -flto -fprofile-instr-generate)
                set_property(TARGET ${TargetName} APPEND_STRING PROPERTY LINK_FLAGS " -flto -fuse-ld=gold -fprofile-instr-generate")
            endif(UPPERCASE_CMAKE_BUILD_TYPE STREQUAL RELEASE OR UPPERCASE_CMAKE_BUILD_TYPE STREQUAL RELWITHDEBINFO)
        endif(CLR_CMAKE_HOST_WIN32)
    elseif(CLR_CMAKE_PGO_OPTIMIZE)
        if(CLR_CMAKE_HOST_WIN32)
            set(ProfileFileName "${TargetName}.pgd")
        else(CLR_CMAKE_HOST_WIN32)
            set(ProfileFileName "coreclr.profdata")
        endif(CLR_CMAKE_HOST_WIN32)

        file(TO_NATIVE_PATH
            "${CLR_CMAKE_OPTDATA_PATH}/data/${ProfileFileName}"
            ProfilePath
        )

        # If we don't have profile data availble, gracefully fall back to a non-PGO opt build
        if(NOT EXISTS ${ProfilePath})
            message("PGO data file NOT found: ${ProfilePath}")
        elseif(CMAKE_GENERATOR MATCHES "Visual Studio")
            # MSVC is sensitive to exactly the options passed during PGO optimization and Ninja and
            # MSBuild differ slightly (but not meaningfully for runtime behavior)
            message("Cannot use PGO optimization built with Ninja from MSBuild. Re-run build with Ninja to apply PGO information")
        else(NOT EXISTS ${ProfilePath})
            if(CLR_CMAKE_HOST_WIN32)
                set_property(TARGET ${TargetName} APPEND_STRING PROPERTY LINK_FLAGS_RELEASE        " /LTCG /USEPROFILE:PGD=\"${ProfilePath}\"")
                set_property(TARGET ${TargetName} APPEND_STRING PROPERTY LINK_FLAGS_RELWITHDEBINFO " /LTCG /USEPROFILE:PGD=\"${ProfilePath}\"")
            else(CLR_CMAKE_HOST_WIN32)
                if(UPPERCASE_CMAKE_BUILD_TYPE STREQUAL RELEASE OR UPPERCASE_CMAKE_BUILD_TYPE STREQUAL RELWITHDEBINFO)
                    if((CMAKE_CXX_COMPILER_ID MATCHES "Clang") AND (NOT CMAKE_CXX_COMPILER_VERSION VERSION_LESS 3.6))
                        if(HAVE_LTO)
                            target_compile_options(${TargetName} PRIVATE -flto -fprofile-instr-use=${ProfilePath} -Wno-profile-instr-out-of-date -Wno-profile-instr-unprofiled)
                            set_property(TARGET ${TargetName} APPEND_STRING PROPERTY LINK_FLAGS " -flto -fuse-ld=gold -fprofile-instr-use=${ProfilePath}")
                        else(HAVE_LTO)
                            message(WARNING "LTO is not supported, skipping profile guided optimizations")
                        endif(HAVE_LTO)
                    else((CMAKE_CXX_COMPILER_ID MATCHES "Clang") AND (NOT CMAKE_CXX_COMPILER_VERSION VERSION_LESS 3.6))
                        message(WARNING "PGO is not supported; Clang 3.6 or later is required for profile guided optimizations")
                    endif((CMAKE_CXX_COMPILER_ID MATCHES "Clang") AND (NOT CMAKE_CXX_COMPILER_VERSION VERSION_LESS 3.6))
                endif(UPPERCASE_CMAKE_BUILD_TYPE STREQUAL RELEASE OR UPPERCASE_CMAKE_BUILD_TYPE STREQUAL RELWITHDEBINFO)
            endif(CLR_CMAKE_HOST_WIN32)
        endif(NOT EXISTS ${ProfilePath})
    endif(CLR_CMAKE_PGO_INSTRUMENT)
endfunction(add_pgo)
