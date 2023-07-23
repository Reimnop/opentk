﻿using System;
using System.Collections.Generic;
using System.Linq;
using Generator.Utility.Extensions;
using Generator.Utility;
using Generator.Writing;
using Generator.Parsing;
using System.Net.Http.Headers;
using System.Collections.Immutable;

namespace Generator.Process
{
    public static class Processor
    {

        // These types are only used to pass data from ProcessSpec to GetOutputApiFromRequireTags.
        private record ProcessedGLInformation(
            Dictionary<string, OverloadedFunction> AllFunctions,
            Dictionary<OutputApi, Dictionary<string, EnumGroupMember>> AllEnumsPerAPI,
            List<EnumGroupInfo> AllEnumGroups);

        public record OverloadedFunction(
            NativeFunction NativeFunction,
            Dictionary<OutputApi, CommandDocumentation> Documentation,
            Overload[] Overloads,
            bool ChangeNativeName);

        public sealed record EnumGroupInfo(
            string GroupName,
            bool IsFlags)
        {
            // To deduplicate these correctly we need special logic for the IsFlags bool
            // so we don't consider it in the equality check and hashcode to allow for that.
            //
            // Example:
            // PathFontStyle uses GL_NONE which is not marked as bitmask
            // but other entries such as GL_BOLD_BIT_NV is marked as bitmask.
            //
            // When this case happens we want to consider the entire groupName as a bitmask.
            //
            // In the current spec this case only happens for PathFontStyle.
            // - 2021-07-04
            public bool Equals(EnumGroupInfo? other) =>
                other?.GroupName == GroupName;

            public override int GetHashCode() =>
                HashCode.Combine(GroupName);
        };

        record RequireEntryInfo(
            string Vendor,
            // FIXME: Make this just one string?
            Version? IntroducedInVersion,
            string? IntroducedInExtension,
            RequireEntry Entry);

        record RemoveEntryInfo(
            Version RemovedInVersion,
            RemoveEntry Entry);

        public static OutputData ProcessSpec2(Specification2 spec, Documentation docs)
        {
            // The first thing we do is process all of the vendorFunctions defined into a dictionary of Functions.
            List<NativeFunction> allEntryPoints = new List<NativeFunction>();
            Dictionary<string, OverloadedFunction> allFunctions = new Dictionary<string, OverloadedFunction>(spec.Commands.Count);
            foreach (Command command in spec.Commands)
            {
                NativeFunction nativeFunction = MakeNativeFunction(command);
                Dictionary<OutputApi, CommandDocumentation> functionDocumentation = MakeDocumentationForNativeFunction(nativeFunction, docs);
                OverloadedFunction overloadedFunction = GenerateOverloads(nativeFunction, functionDocumentation);

                allEntryPoints.Add(nativeFunction);
                allFunctions.Add(nativeFunction.EntryPoint, overloadedFunction);
            }

            Dictionary<OutputApi, Dictionary<string, EnumGroupMember>> allEnumsPerAPI = new Dictionary<OutputApi, Dictionary<string, EnumGroupMember>>();
            Dictionary<OutputApi, HashSet<EnumGroupInfo>> allEnumGroups = new Dictionary<OutputApi, HashSet<EnumGroupInfo>>();
            foreach (OutputApi outputApi in Enum.GetValues<OutputApi>())
            {
                if (outputApi == OutputApi.Invalid) continue;
                allEnumsPerAPI.Add(outputApi, new Dictionary<string, EnumGroupMember>());
                allEnumGroups.Add(outputApi, new HashSet<EnumGroupInfo>());
            }

            foreach (EnumEntry @enum in spec.Enums)
            {
                bool isFlag = @enum.Type == EnumType.Bitmask;

                foreach ((string groupName, GLFile @namespace) in @enum.Groups)
                {
                    if (@namespace == GLFile.GL)
                    {
                        AddToGroup(allEnumGroups, OutputApi.GL, groupName, isFlag);
                        AddToGroup(allEnumGroups, OutputApi.GLCompat, groupName, isFlag);
                        AddToGroup(allEnumGroups, OutputApi.GLES1, groupName, isFlag);
                        AddToGroup(allEnumGroups, OutputApi.GLES2, groupName, isFlag);
                    }
                    else if (@namespace == GLFile.WGL)
                    {
                        AddToGroup(allEnumGroups, OutputApi.WGL, groupName, isFlag);
                    }
                    else if (@namespace == GLFile.GLX)
                    {
                        AddToGroup(allEnumGroups, OutputApi.GLX, groupName, isFlag);
                    }

                    static void AddToGroup(Dictionary<OutputApi, HashSet<EnumGroupInfo>> allEnumGroups, OutputApi api, string groupName, bool isFlag)
                    {
                        // If the first groupNameToEnumGroup tag wasn't flagged as a bitmask, but later ones in the same groupName are.
                        // Then we want the groupName to be considered a bitmask.
                        if (allEnumGroups[api].TryGetValue(new EnumGroupInfo(groupName, isFlag), out EnumGroupInfo? actual))
                        {
                            // In the current spec this case never happens, but it could.
                            // - 2021-07-04
                            if (isFlag == true && actual.IsFlags == false)
                            {
                                allEnumGroups[api].Remove(actual);
                                allEnumGroups[api].Add(actual with { IsFlags = true });
                            }
                        }
                        else
                        {
                            allEnumGroups[api].Add(new EnumGroupInfo(groupName, isFlag));
                        }
                    }
                }

                EnumGroupMember data = new EnumGroupMember(NameMangler.MangleEnumName(@enum.Name), @enum.Value, @enum.Groups, isFlag);

                if (@enum.Api == EnumAPI.None)
                {
                    throw new Exception();
                }

                if (@enum.Api.HasFlag(EnumAPI.GL))
                {
                    allEnumsPerAPI.AddToNestedDict(OutputApi.GL, @enum.Name, data);
                }

                if (@enum.Api.HasFlag(EnumAPI.GLCompat))
                {
                    allEnumsPerAPI.AddToNestedDict(OutputApi.GLCompat, @enum.Name, data);
                }

                if (@enum.Api.HasFlag(EnumAPI.GLES1))
                {
                    allEnumsPerAPI.AddToNestedDict(OutputApi.GLES1, @enum.Name, data);
                }

                if (@enum.Api.HasFlag(EnumAPI.GLES2))
                {
                    allEnumsPerAPI.AddToNestedDict(OutputApi.GLES2, @enum.Name, data);
                }

                if (@enum.Api.HasFlag(EnumAPI.WGL))
                {
                    allEnumsPerAPI.AddToNestedDict(OutputApi.WGL, @enum.Name, data);
                }

                if (@enum.Api.HasFlag(EnumAPI.GLX))
                {
                    allEnumsPerAPI.AddToNestedDict(OutputApi.GLX, @enum.Name, data);
                }
            }

            List<Namespace> outputNamespaces = new List<Namespace>();

            foreach (var (api, functions, enums) in spec.APIs)
            {
                // FIXME: Probably make these the same enum!
                OutputApi outAPI = api switch
                {
                    InputAPI.GL => OutputApi.GL,
                    // FIXME?
                    //InputAPI.GLCompat => OutputApi.GLCompat,
                    InputAPI.GLES1 => OutputApi.GLES1,
                    InputAPI.GLES2 => OutputApi.GLES2,
                    InputAPI.WGL => OutputApi.WGL,
                    InputAPI.GLX => OutputApi.GLX,

                    _ => throw new Exception(),
                };

                outputNamespaces.Add(CreateOutputAPI(outAPI));

                if (outAPI == OutputApi.GL)
                {
                    outputNamespaces.Add(CreateOutputAPI(OutputApi.GLCompat));
                }

                Namespace CreateOutputAPI(OutputApi outAPI)
                {
                    bool removeFunctions = outAPI switch
                    {
                        OutputApi.GL => true,
                        OutputApi.GLES2 => true,
                        _ => false,
                    };

                    HashSet<string> groupsReferencedByFunctions = new HashSet<string>();

                    Dictionary<string, EnumGroupMember>? enumsDict = allEnumsPerAPI[outAPI];

                    // FIXME: Make api an OutputAPI

                    Dictionary<string, HashSet<OverloadedFunction>> functionsByVendor = new Dictionary<string, HashSet<OverloadedFunction>>();
                    
                    HashSet<EnumGroupMember> theAllEnumGroup = new HashSet<EnumGroupMember>();

                    foreach (var functionRef in functions)
                    {
                        if (allFunctions.TryGetValue(functionRef.EntryPoint, out OverloadedFunction? overloadedFunction))
                        {
                            bool referenced = false;

                            if (functionRef.AddedIn != null)
                            {
                                if (removeFunctions && (functionRef.RemovedIn != null || functionRef.Profile == GLProfile.Compatibility))
                                {
                                    // Do not add this function
                                }
                                else
                                {
                                    functionsByVendor.AddToNestedHashSet("", overloadedFunction);
                                    
                                    referenced = true;
                                }
                            }

                            foreach (var extension in functionRef.PartOfExtensions)
                            {
                                functionsByVendor.AddToNestedHashSet(extension.Vendor, overloadedFunction);

                                referenced = true;
                            }

                            if (referenced)
                            {
                                groupsReferencedByFunctions.UnionWith(overloadedFunction.NativeFunction.ReferencedEnumGroups);
                            }
                        }
                        else
                        {
                            // FIXME!
                            /*if (GeneratorSettings.Settings.IgnoreFunctions.Contains(functionRef.EntryPoint))
                            {
                                // We are ignoring this function.
                            }
                            else
                            {
                                throw new Exception($"Could not find function '{functionRef.EntryPoint}'!");
                            }*/
                        }
                    }

                    Dictionary<string, List<EnumGroupMember>> groupNameToEnumGroup = new Dictionary<string, List<EnumGroupMember>>();

                    foreach (var enumRef in enums)
                    {
                        if (removeFunctions)
                        {
                            // FIXME: Should we check the profile of the extension??
                            if (enumRef.RemovedIn != null || enumRef.Profile == GLProfile.Compatibility)
                            {
                                // FIXME: Add the enum if an extension uses it??
                                continue;
                            }
                        }

                        // FIXME! This is a big hack!
                        // We don't want to process this "enum" as it is a string.
                        if (enumRef.EnumName == "GLX_EXTENSION_NAME") continue;

                        if (enumsDict.TryGetValue(enumRef.EnumName, out EnumGroupMember? @enum))
                        {
                            // FIXME: Consider namespaces.
                            foreach (var (groupName, _) in @enum.Groups)
                            {
                                if (groupNameToEnumGroup.TryGetValue(groupName, out List<EnumGroupMember>? groupMembers) == false)
                                {
                                    groupMembers = new List<EnumGroupMember>();
                                    groupNameToEnumGroup.Add(groupName, groupMembers);
                                }

                                if (groupMembers.Find(g => g.Name == @enum.Name) == null)
                                {
                                    groupMembers.Add(@enum);
                                }
                            }

                            if (@enum.Value <= uint.MaxValue)
                            {
                                theAllEnumGroup.Add(@enum);
                            }
                        }
                        else
                        {
                            throw new Exception($"Could not find any enum called '{enumRef.EnumName}'.");
                        }
                    }

                    // Go through all vendorFunctions and build up a Dictionary from enumName groups to function using them
                    Dictionary<string, List<(string Vendor, NativeFunction Function)>> enumGroupToNativeFunctionsUsingThatEnumGroup = new Dictionary<string, List<(string Vendor, NativeFunction Function)>>();
                    foreach (var (vendor, vendorFunctions) in functionsByVendor)
                    {
                        foreach (var function in vendorFunctions)
                        {
                            foreach (var group in function.NativeFunction.ReferencedEnumGroups)
                            {
                                if (enumGroupToNativeFunctionsUsingThatEnumGroup.TryGetValue(group, out var listOfFunctions) == false)
                                {
                                    listOfFunctions = new List<(string Vendor, NativeFunction Function)>();
                                    enumGroupToNativeFunctionsUsingThatEnumGroup.Add(group, listOfFunctions);
                                }

                                if (listOfFunctions.Contains((vendor, function.NativeFunction)) == false)
                                {
                                    listOfFunctions.Add((vendor, function.NativeFunction));
                                }
                            }
                        }
                    }

                    // Go through all of the groupNameToEnumGroup and put them into their groups

                    // Add keys + lists for all enumName names
                    List<EnumGroup> finalGroups = new List<EnumGroup>();

                    List<EnumGroupMember> allEnumGroup = theAllEnumGroup.ToList();
                    allEnumGroup.Sort((e1, e2) =>
                    {
                        int comp = e1.Value.CompareTo(e2.Value);
                        if (comp == 0)
                        {
                            return e1.Name.CompareTo(e2.Name);
                        }
                        else
                        {
                            return comp;
                        }
                    });

                    // Add the All enumName groupName
                    finalGroups.Add(new EnumGroup("All", false, allEnumGroup, null));

                    foreach ((string groupName, bool isFlags) in allEnumGroups[outAPI])
                    {
                        groupNameToEnumGroup.TryGetValue(groupName, out List<EnumGroupMember>? members);
                        members ??= new List<EnumGroupMember>();

                        // SpecialNumbers is not an enumName groupName that we want to output.
                        // We handle these entries differently as some of the entries don't fit in an int.
                        if (groupName == "SpecialNumbers")
                            continue;

                        // Remove all empty enumName groups, except the empty groups referenced by included vendorFunctions.
                        // In GL 4.1 to 4.5 there are vendorFunctions that use the groupName "ShaderBinaryFormat"
                        // while not including any members for that enumName groupName.
                        // This is needed to solve that case.
                        if (members.Count <= 0 && groupsReferencedByFunctions.Contains(groupName) == false)
                            continue;

                        if (enumGroupToNativeFunctionsUsingThatEnumGroup.TryGetValue(groupName, out var functionsUsingEnumGroup) == false)
                        {
                            functionsUsingEnumGroup = null;
                        }

                        // If there is a list, sort it by name
                        if (functionsUsingEnumGroup != null)
                            functionsUsingEnumGroup.Sort((f1, f2) => {
                                // We want to prioritize "core" vendorFunctions before extensions.
                                if (f1.Vendor == "" && f2.Vendor != "") return -1;
                                if (f1.Vendor != "" && f2.Vendor == "") return 1;

                                return f1.Function.FunctionName.CompareTo(f2.Function.FunctionName);
                            });

                        members.Sort((m1, m2) =>
                        {
                            int comp = m1.Value.CompareTo(m2.Value);
                            if (comp == 0)
                            {
                                return m1.Name.CompareTo(m2.Name);
                            }
                            else
                            {
                                return comp;
                            }
                        });

                        finalGroups.Add(new EnumGroup(groupName, isFlags, members, functionsUsingEnumGroup));
                    }

                    // Group vendors
                    // Group groupNameToEnumGroup
                    // Lookup documentation
                    Dictionary<string, GLVendorFunctions> vendors = new Dictionary<string, GLVendorFunctions>();
                    foreach ((string vendor, HashSet<OverloadedFunction> overloadedFunctions) in functionsByVendor)
                    {
                        foreach (OverloadedFunction overloadedFunction in overloadedFunctions)
                        {
                            if (!vendors.TryGetValue(vendor, out GLVendorFunctions? group))
                            {
                                group = new GLVendorFunctions(new List<Writing.OverloadedFunction>(), new HashSet<NativeFunction>());
                                vendors.Add(vendor, group);
                            }

                            group.Functions.Add(new Writing.OverloadedFunction(overloadedFunction.NativeFunction, overloadedFunction.Overloads));

                            if (overloadedFunction.ChangeNativeName)
                            {
                                group.NativeFunctionsWithPostfix.Add(overloadedFunction.NativeFunction);
                            }
                        }
                    }

                    SortedDictionary<string, GLVendorFunctions> sortedVendors = new SortedDictionary<string, GLVendorFunctions>(vendors);
                    foreach (var (vendor, vendorFunctions) in sortedVendors)
                    {
                        vendorFunctions.Functions.Sort();
                    }

                    Dictionary<NativeFunction, FunctionDocumentation> documentation = new Dictionary<NativeFunction, FunctionDocumentation>();
                    foreach (var (vendor, vendorFunctions) in functionsByVendor)
                    {
                        foreach (var function in vendorFunctions)
                        {
                            if (function.Documentation.TryGetValue(outAPI, out CommandDocumentation? commandDocumentation))
                            {
                                var func = functions.Find(f => f.EntryPoint == function.NativeFunction.EntryPoint);

                                if (func == null)
                                {
                                    throw new Exception($"Could not find function {function.NativeFunction.EntryPoint}!");
                                }

                                List<string> addedIn = new List<string>();
                                if (func.AddedIn != null)
                                {
                                    addedIn.Add($"v{func.AddedIn.Major}.{func.AddedIn.Minor}");
                                }

                                foreach (var extension in func.PartOfExtensions)
                                {
                                    addedIn.Add(extension.Name);
                                }

                                List<string> removedIn = new List<string>();
                                if (func.RemovedIn != null)
                                {
                                    removedIn.Add($"v{func.RemovedIn.Major}.{func.RemovedIn.Minor}");
                                }

                                // FIXME: Added and removed information.
                                documentation[function.NativeFunction] = new FunctionDocumentation(
                                    commandDocumentation.Name,
                                    commandDocumentation.Purpose,
                                    commandDocumentation.Parameters,
                                    commandDocumentation.RefPagesLink,
                                    addedIn,
                                    removedIn
                                    );
                            }
                            else
                            {
                                if (vendor == "")
                                {
                                    Logger.Warning($"{function.NativeFunction.EntryPoint} doesn't have any documentation for {api}");

                                    var func = functions.Find(f => f.EntryPoint == function.NativeFunction.EntryPoint);

                                    if (func == null)
                                    {
                                        throw new Exception($"Could not find function {function.NativeFunction.EntryPoint}!");
                                    }

                                    List<string> addedIn = new List<string>();
                                    if (func.AddedIn != null)
                                    {
                                        addedIn.Add($"v{func.AddedIn.Major}.{func.AddedIn.Minor}");
                                    }

                                    foreach (var extension in func.PartOfExtensions)
                                    {
                                        addedIn.Add(extension.Name);
                                    }

                                    List<string> removedIn = new List<string>();
                                    if (func.RemovedIn != null)
                                    {
                                        removedIn.Add($"v{func.RemovedIn.Major}.{func.RemovedIn.Minor}");
                                    }

                                    documentation[function.NativeFunction] = new FunctionDocumentation(
                                        function.NativeFunction.EntryPoint,
                                        "",
                                        Array.Empty<ParameterDocumentation>(),
                                        // TODO: Is it possible to get the functionRef spec file and link to it here?
                                        null,
                                        addedIn,
                                        removedIn);
                                }
                                else
                                {
                                    var func = functions.Find(f => f.EntryPoint == function.NativeFunction.EntryPoint);

                                    if (func == null)
                                    {
                                        throw new Exception($"Could not find function {function.NativeFunction.EntryPoint}!");
                                    }

                                    List<string> addedIn = new List<string>();
                                    if (func.AddedIn != null)
                                    {
                                        addedIn.Add($"v{func.AddedIn.Major}.{func.AddedIn.Minor}");
                                    }

                                    foreach (var extension in func.PartOfExtensions)
                                    {
                                        addedIn.Add(extension.Name);
                                    }

                                    List<string> removedIn = new List<string>();
                                    if (func.RemovedIn != null)
                                    {
                                        removedIn.Add($"v{func.RemovedIn.Major}.{func.RemovedIn.Minor}");
                                    }

                                    documentation[function.NativeFunction] = new FunctionDocumentation(
                                        function.NativeFunction.EntryPoint,
                                        "",
                                        Array.Empty<ParameterDocumentation>(),
                                        // TODO: Is it possible to get the extension spec file and link to it here?
                                        null,
                                        addedIn,
                                        removedIn);
                                    // Extensions don't have documentation (yet?)
                                }
                            }
                        }
                    }

                    return new Namespace(outAPI, sortedVendors, finalGroups, documentation);
                    //return new GLOutputApi(outAPI, sortedVendors, finalGroups, documentation);
                }
            }

            // FIXME: This requires us to merge all input data!
            // FIXME: Potentially split the GLES function pointers from the GL ones.
            List<Pointers> pointers = new List<Pointers>();

            // FIXME FIXME FIXME: Super hacky temp fix until we merge the parse data!
            if (NameMangler.Settings.FunctionPrefix == "gl")
            {
                pointers.Add(CreatePointersList(GLFile.GL, outputNamespaces));
            }
            else if (NameMangler.Settings.FunctionPrefix == "wgl")
            {
                pointers.Add(CreatePointersList(GLFile.WGL, outputNamespaces));
            }
            else if (NameMangler.Settings.FunctionPrefix == "glx")
            {
                pointers.Add(CreatePointersList(GLFile.GLX, outputNamespaces));
            }

            /*
            pointers.Add(CreatePointersList(GLFile.GL, outputNamespaces));
            pointers.Add(CreatePointersList(GLFile.WGL, outputNamespaces));
            pointers.Add(CreatePointersList(GLFile.GLX, outputNamespaces));
            */

            return new OutputData(pointers, outputNamespaces);

            Pointers CreatePointersList(GLFile file, List<Namespace> namespaces)
            {
                List<NativeFunction> allFunctions = new List<NativeFunction>();
                foreach (Namespace @namespace in namespaces)
                {
                    bool addFunctions = false;
                    switch (file)
                    {
                        case GLFile.GL:
                            if (@namespace.Name == OutputApi.GL ||
                                @namespace.Name == OutputApi.GLCompat ||
                                @namespace.Name == OutputApi.GLES1 ||
                                @namespace.Name == OutputApi.GLES2)
                            {
                                addFunctions = true;
                            }
                            break;
                        case GLFile.WGL:
                            if (@namespace.Name == OutputApi.WGL)
                            {
                                addFunctions = true;
                            }
                            break;
                        case GLFile.GLX:
                            if (@namespace.Name == OutputApi.GLX)
                            {
                                addFunctions = true;
                            }
                            break;
                    }

                    if (addFunctions)
                    {
                        foreach (var (_, functions) in @namespace.Vendors)
                        {
                            foreach (var function in functions.Functions)
                            {
                                if (allFunctions.Contains(function.NativeFunction) == false)
                                {
                                    allFunctions.Add(function.NativeFunction);
                                }
                            }
                        }
                    }
                }

                allFunctions.Sort((f1, f2) => f1.EntryPoint.CompareTo(f2.EntryPoint));

                return new Pointers(file, allFunctions);
            }
        }

        public static NativeFunction MakeNativeFunction(Command command)
        {
            string functionName = NameMangler.MangleFunctionName(command.EntryPoint);

            HashSet<string> referencedEnumGroups = new HashSet<string>();

            List<Parameter> parameters = new List<Parameter>();
            foreach (GLParameter parameter in command.Parameters)
            {
                BaseCSType type = MakeCSType(parameter.Type.Type, parameter.Type.Handle, parameter.Type.Group);
                // FIXME: Maybe we want to do some kind of processing on parameter.Kind to not pass it directly as it is in gl.xml
                parameters.Add(new Parameter(type, parameter.Kinds, NameMangler.MangleParameterName(parameter.Name), parameter.Length));
                if (parameter.Type.Group != null)
                {
                    // FIXME: namespace!
                    referencedEnumGroups.Add(parameter.Type.Group.Name);
                }
            }

            BaseCSType returnType = MakeCSType(command.ReturnType.Type, command.ReturnType.Handle, command.ReturnType.Group);
            if (command.ReturnType.Group != null)
            {
                // FIXME: namespace!
                referencedEnumGroups.Add(command.ReturnType.Group.Name);
            }

            return new NativeFunction(command.EntryPoint, functionName, parameters, returnType, referencedEnumGroups.ToArray());
        }

        public static BaseCSType MakeCSType(GLType type, HandleType? handle, GroupRef? group)
        {
            switch (type)
            {
                case GLPointerType pt:
                    return new CSPointer(MakeCSType(pt.BaseType, handle, group), pt.Constant);

                case GLBaseType bt:
                    {
                        if (Options.UseTypesafeGLHandles && handle != null)
                        {
                            return new CSStructPrimitive(handle.Value.ToString(), bt.Constant, new CSPrimitive("int", bt.Constant));
                        }

                        // To make OpenTK 5 more like OpenTK 4 we want handles to be int instead of uint
                        if (handle != null)
                        {
                            return new CSPrimitive("int", bt.Constant);
                        }

                        // For now we only expect int and uint to be able to be turned into groupNameToEnumGroup.
                        // - 2022-08-09
                        // FIXME: We might want to make sure that the underlying type for the enumName groupName is the same as the parameter groupName.
                        //   Right now we blindly substituting the type for the enumName.
                        if (group != null && (bt.Type == PrimitiveType.Int || bt.Type == PrimitiveType.Uint))
                        {
                            Console.WriteLine($"Making {bt} into group {group}");
                            CSPrimitive baseType = bt.Type switch
                            {
                                PrimitiveType.Int => new CSPrimitive("int", bt.Constant),
                                PrimitiveType.Uint => new CSPrimitive("uint", bt.Constant),
                                _ => throw new Exception("This should not happen!"),
                            };

                            return new CSEnum(group.Name, baseType, bt.Constant);
                        }
                        return bt.Type switch
                        {
                            // C# primitive types
                            PrimitiveType.Void => new CSVoid(bt.Constant),
                            PrimitiveType.Byte => new CSPrimitive("byte", bt.Constant),
                            PrimitiveType.Sbyte => new CSPrimitive("sbyte", bt.Constant),
                            PrimitiveType.Short => new CSPrimitive("short", bt.Constant),
                            PrimitiveType.Ushort => new CSPrimitive("ushort", bt.Constant),
                            PrimitiveType.Int => new CSPrimitive("int", bt.Constant),
                            PrimitiveType.Uint => new CSPrimitive("uint", bt.Constant),
                            PrimitiveType.Long => new CSPrimitive("long", bt.Constant),
                            PrimitiveType.Ulong => new CSPrimitive("ulong", bt.Constant),
                            // This might need an include, but the spec doesn't use this type
                            // so we don't really need to do anything...
                            PrimitiveType.Half => new CSStructPrimitive("Half", bt.Constant, new CSPrimitive("ushort", bt.Constant)),
                            PrimitiveType.Float => new CSPrimitive("float", bt.Constant),
                            PrimitiveType.Double => new CSPrimitive("double", bt.Constant),

                            // C interop types
                            PrimitiveType.Bool8 => new CSBool8(bt.Constant),
                            PrimitiveType.Bool32 => new CSBool32(bt.Constant),
                            PrimitiveType.Char8 => new CSChar8(bt.Constant),


                            // Enum
                            PrimitiveType.Enum => new CSEnum(group?.Name ?? "All", new CSPrimitive("uint", bt.Constant), bt.Constant),

                            // Pointers
                            PrimitiveType.IntPtr => new CSPrimitive("IntPtr", bt.Constant),
                            PrimitiveType.Nint => new CSPrimitive("nint", bt.Constant),
                            PrimitiveType.VoidPtr => new CSPointer(new CSVoid(false), bt.Constant),

                            // FIXME: Output the GLHandleARB again...
                            PrimitiveType.GLHandleARB => new CSStructPrimitive("GLHandleARB", bt.Constant, new CSPrimitive("IntPtr", bt.Constant)),

                            PrimitiveType.GLSync => new CSStructPrimitive("GLSync", bt.Constant, new CSPrimitive("IntPtr", bt.Constant)),

                            // OpenCL structs
                            PrimitiveType.CLContext => new CSStructPrimitive("CLContext", bt.Constant, new CSPrimitive("IntPtr", bt.Constant)),
                            PrimitiveType.CLEvent => new CSStructPrimitive("CLEvent", bt.Constant, new CSPrimitive("IntPtr", bt.Constant)),

                            // Function pointer types
                            PrimitiveType.GLDebugProc => new CSFunctionPointer("GLDebugProc", bt.Constant),
                            PrimitiveType.GLDebugProcARB => new CSFunctionPointer("GLDebugProcARB", bt.Constant),
                            PrimitiveType.GLDebugProcKHR => new CSFunctionPointer("GLDebugProcKHR", bt.Constant),
                            PrimitiveType.GLDebugProcAMD => new CSFunctionPointer("GLDebugProcAMD", bt.Constant),
                            PrimitiveType.GLDebugProcNV => new CSFunctionPointer("GLDebugProcNV", bt.Constant),
                            PrimitiveType.GLVulkanProcNV => new CSFunctionPointer("GLVulkanProcNV", bt.Constant),


                            // WGL
                            PrimitiveType.WGL_Proc => new CSFunctionPointer("???", bt.Constant),
                            
                            PrimitiveType.WGL_Rect => new CSStruct("Rect", bt.Constant),
                            PrimitiveType.WGL_LPString => new CSPointer(new CSChar16(true), bt.Constant),
                            PrimitiveType.WGL_COLORREF => new CSStructPrimitive("ColorRef", bt.Constant, new CSPrimitive("uint", false)),
                            PrimitiveType.WGL_LAYERPLANEDESCRIPTOR => new CSStruct("LayerPlaneDescriptor", bt.Constant),
                            PrimitiveType.WGL_PIXELFORMATDESCRIPTOR => new CSStruct("PixelFormatDescriptor", bt.Constant),
                            PrimitiveType.WGL_GPU_DEVICE => new CSStruct("_GPU_DEVICE", bt.Constant),
                            PrimitiveType.WGL_PGPU_DEVICE => new CSPointer(new CSStruct("_GPU_DEVICE", false), bt.Constant),

                            PrimitiveType.GLX_Colormap => new CSStructPrimitive("Colormap", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_Display => new CSStruct("Display", bt.Constant), // FIXME: This is just a struct?
                            PrimitiveType.GLX_Font => new CSStructPrimitive("Font", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_Pixmap => new CSStructPrimitive("Pixmap", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_Screen => new CSStruct("Screen", bt.Constant),
                            PrimitiveType.GLX_Status => new CSPrimitive("int", bt.Constant),
                            PrimitiveType.GLX_Window => new CSStructPrimitive("Window", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_EXTFuncPtr => new CSFunctionPointer("__GLXextFuncPtr", bt.Constant),
                            PrimitiveType.GLX_XVisualInfo => new CSStruct("XVisualInfo", bt.Constant),

                            // FIXME: These types are conditionally removed from the header if _DM_BUFFER_H_ is not defined
                            // Should we have some way to say that specific functions should be ignored?
                            PrimitiveType.GLX_DMbuffer => new CSVoid(bt.Constant),
                            PrimitiveType.GLX_DMparams => new CSVoid(bt.Constant),

                            // FIXME: These types are conditionally removed from the header if _VL_H_ is not defined.
                            // Should we have some way to say that specific functions should be ignored?
                            PrimitiveType.GLX_VLNode => new CSVoid(bt.Constant),
                            PrimitiveType.GLX_VLPath => new CSVoid(bt.Constant),
                            PrimitiveType.GLX_VLServer => new CSVoid(bt.Constant),

                            PrimitiveType.GLX_FBConfigID => new CSStructPrimitive("FBConfigID", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_FBConfig => new CSStructPrimitive("GLXFBConfig", bt.Constant, new CSPrimitive("IntPtr", bt.Constant)),
                            PrimitiveType.GLX_ContextID => new CSStructPrimitive("GLXContextID", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_Context => new CSStructPrimitive("GLXContext", bt.Constant, new CSPrimitive("IntPtr", bt.Constant)),
                            PrimitiveType.GLX_GLXPixmap => new CSStructPrimitive("GLXPixmap", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_GLXDrawable => new CSStructPrimitive("GLXDrawable", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_GLXWindow => new CSStructPrimitive("GLXWindow", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_GLXPbuffer => new CSStructPrimitive("GLXPbuffer", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_VideoCaptureDeviceNV => new CSStructPrimitive("GLXVideoCaptureDeviceNV", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_VideoDeviceNV => new CSStructPrimitive("GLXVideoDeviceNV", bt.Constant, new CSPrimitive("uint", bt.Constant)),
                            PrimitiveType.GLX_VideoSourceSGIX => new CSStructPrimitive("GLXVideoSourceSGIX", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_FBConfigIDSGIX => new CSStructPrimitive("GLXFBConfigIDSGIX", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_FBConfigSGIX => new CSStructPrimitive("GLXFBConfigSGIX", bt.Constant, new CSPrimitive("IntPtr", bt.Constant)),
                            PrimitiveType.GLX_GLXPbufferSGIX => new CSStructPrimitive("GLXPbufferSGIX", bt.Constant, new CSPrimitive("nuint", bt.Constant)),
                            PrimitiveType.GLX_GLXPbufferClobberEvent => new CSStruct("GLXPbufferClobberEvent", bt.Constant),
                            PrimitiveType.GLX_GLXBufferSwapComplete => new CSStruct("GLXBufferSwapComplete", bt.Constant),
                            PrimitiveType.GLX_GLXEvent => new CSStruct("GLXEvent", bt.Constant),
                            PrimitiveType.GLX_GLXStereoNotifyEventEXT => new CSStruct("GLXStereoNotifyEventEXT", bt.Constant),
                            PrimitiveType.GLX_GLXBufferClobberEventSGIX => new CSStruct("GLXBufferClobberEventSGIX", bt.Constant),
                            PrimitiveType.GLX_GLXHyperpipeNetworkSGIX => new CSStruct("GLXHyperpipeNetworkSGIX", bt.Constant),
                            PrimitiveType.GLX_GLXHyperpipeConfigSGIX => new CSStruct("GLXHyperpipeConfigSGIX", bt.Constant),
                            PrimitiveType.GLX_GLXPipeRect => new CSStruct("GLXPipeRect", bt.Constant),
                            PrimitiveType.GLX_GLXPipeRectLimits => new CSStruct("GLXPipeRectLimits", bt.Constant),

                            PrimitiveType.Invalid => throw new Exception(),
                            _ => throw new Exception(),
                        };
                    }
                default:
                    throw new Exception();
            }
        }

        public static Dictionary<OutputApi, CommandDocumentation> MakeDocumentationForNativeFunction(NativeFunction function, Documentation documentation)
        {
            Dictionary<OutputApi, CommandDocumentation> commandDocs = new Dictionary<OutputApi, CommandDocumentation>();

            foreach (var (version, versionDocumentation) in documentation.VersionDocumentation)
            {
                if (versionDocumentation.Commands.TryGetValue(function.EntryPoint, out CommandDocumentation? commandDoc))
                {
                    if (function.Parameters.Count != commandDoc.Parameters.Length)
                    {
                        Logger.Warning($"Function {function.EntryPoint} has differnet number of parameters than the parsed documentation. (gl.xml:{function.Parameters.Count}, documentation:{commandDoc.Parameters.Length})");
                    }

                    for (int i = 0; i < Math.Min(function.Parameters.Count, commandDoc.Parameters.Length); i++)
                    {
                        if (function.Parameters[i].Name != commandDoc.Parameters[i].Name)
                        {
                            Logger.Warning($"[{version}][{function.EntryPoint}] Function parameter '{function.Parameters[i].Name}' doesn't have the same name in the documentation. ('{commandDoc.Parameters[i].Name}')");
                        }
                    }

                    commandDocs.Add(version, commandDoc);
                }
            }

            return commandDocs;
        }

        // Maybe we can do the return type overloading in a post processing step?
        public static OverloadedFunction GenerateOverloads(NativeFunction nativeFunction, Dictionary<OutputApi, CommandDocumentation> functionDocumentation)
        {
            List<Overload> overloads = new List<Overload>
            {
                // Make a "base" overload
                new Overload(null, null, nativeFunction.Parameters.ToArray(), nativeFunction, nativeFunction.ReturnType,
                    new NameTable(), /*"returnValue",*/ Array.Empty<string>(), nativeFunction.FunctionName),
            };

            bool overloadedOnce = false;
            foreach (IOverloader overloader in IOverloader.Overloaders)
            {
                List<Overload> newOverloads = new List<Overload>();
                foreach (Overload overload in overloads)
                {
                    if (overloader.TryGenerateOverloads(overload, out List<Overload>? overloaderOverloads))
                    {
                        overloadedOnce = true;

                        newOverloads.AddRange(overloaderOverloads);
                    }
                    else
                    {
                        newOverloads.Add(overload);
                    }
                }
                // Replace the old overloads with the new overloads
                overloads = newOverloads;
            }
            Overload[] overloadArray = overloadedOnce ? overloads.ToArray() : Array.Empty<Overload>();

            bool changeNativeName = false;
            foreach (Overload overload in overloadArray)
            {
                if (AreSignaturesDifferent(nativeFunction, overload) == false)
                {
                    changeNativeName = true;
                }
            }

            return new OverloadedFunction(nativeFunction, functionDocumentation, overloadArray, changeNativeName);
        }

        private static bool AreSignaturesDifferent(NativeFunction nativeFunction, Overload overload)
        {
            if (nativeFunction.Parameters.Count != overload.InputParameters.Length)
            {
                return true;
            }

            if (overload.OverloadName != nativeFunction.FunctionName)
            {
                return true;
            }

            for (int i = 0; i < nativeFunction.Parameters.Count; i++)
            {
                if (nativeFunction.Parameters[i].Type.Equals(overload.InputParameters[i].Type) == false)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
