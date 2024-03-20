using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SwiftAbiStressGenerator
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: SwiftAbiStressGenerator <num functions> <max parameters|max ret fields> [--gen-return-tests] [--gen-lowering-tests] [--reverse-pinvoke]");
                return;
            }

            int numFunctions = int.Parse(args[0]);
            int maxParametersOrFields = int.Parse(args[1]);
            bool genReturnTests = args.Contains("--gen-return-tests");
            bool genLoweringTests = args.Contains("--gen-lowering-tests");
            bool genReversePinvoke = args.Contains("--reverse-pinvoke");
            bool genParamTests = !genReturnTests && !genLoweringTests && !genReversePinvoke;

            Random rand = new Random(1234);

            StringBuilder swift = new();
            StringWriter swiftWriter = new StringWriter(swift);
            swift.AppendLine("import Foundation");
            swift.AppendLine("");

            if (genParamTests)
            {
                swift.AppendLine("struct HasherFNV1a {");
                swift.AppendLine("");
                swift.AppendLine("    private var hash: UInt = 14_695_981_039_346_656_037");
                swift.AppendLine("    private let prime: UInt = 1_099_511_628_211");
                swift.AppendLine("");
                swift.AppendLine("    mutating func combine<T>(_ val: T) {");
                swift.AppendLine("        for byte in withUnsafeBytes(of: val, Array.init) {");
                swift.AppendLine("            hash ^= UInt(byte)");
                swift.AppendLine("            hash = hash &* prime");
                swift.AppendLine("        }");
                swift.AppendLine("    }");
                swift.AppendLine("");
                swift.AppendLine("    func finalize() -> Int {");
                swift.AppendLine("        Int(truncatingIfNeeded: hash)");
                swift.AppendLine("    }");
                swift.AppendLine("}");
                swift.AppendLine("");
            }

            (string testName, string swiftFuncNamePrefix, string csharpFuncNamePrefix, string mangledNamePrefix) =
                genReturnTests ? ("SwiftRetAbiStress", "swiftRetFunc", "SwiftRetFunc", "Func") : // weird substitution stuff in their mangling means we can't use "swiftRetFunc"...
                genReversePinvoke ? ("SwiftCallbackAbiStress", "swiftCallbackFunc", "SwiftCallbackFunc", "Func") :
                                    ("SwiftAbiStress", "swiftFunc", "SwiftFunc", "swiftFunc");

            List<List<InteropType>> funcParamTypes = new();
            List<InteropType> retTypes = new();

            if (genParamTests || genLoweringTests || genReversePinvoke)
            {
                for (int i = 0; i < numFunctions; i++)
                {
                    int numParams = rand.Next(1, maxParametersOrFields + 1);
                    if (genLoweringTests)
                    {
                        funcParamTypes.Add(new List<InteropType> { GenStructType(rand, numParams, $"F{i}_S") });
                    }
                    else
                    {
                        funcParamTypes.Add(GenParameterTypes(rand, numParams, $"F{i}"));
                    }

                    if (genReversePinvoke)
                    {
                        if (rand.NextDouble() < 0.5)
                        {
                            retTypes.Add(s_primitiveInteropTypes[rand.Next(s_primitiveInteropTypes.Length)]);
                        }
                        else
                        {
                            retTypes.Add(GenStructType(rand, 7, $"F{i}_Ret"));
                        }
                    }
                    else
                    {
                        retTypes.Add(s_primitiveInteropTypes.First());
                    }

                    foreach (InteropType paramType in funcParamTypes.Last())
                    {
                        paramType.GenerateSwift(swift);
                    }

                    if (genReversePinvoke)
                    {
                        retTypes.Last()!.GenerateSwift(swift);

                        swift.Append($"public func {swiftFuncNamePrefix}{i}(f: (");
                        swift.AppendJoin(", ", funcParamTypes.Last()!.Select((t, i) => t.GenerateSwiftUse()));
                        swift.AppendLine($") -> {retTypes.Last()!.GenerateSwiftUse()}) -> {retTypes.Last().GenerateSwiftUse()} {{");

                        Fnv1aHasher hasher = new();
                        Random reverseRand = CreateRandForTest(i);
                        swift.Append("    return f(");
                        bool first = true;
                        foreach (InteropType paramType in funcParamTypes.Last())
                        {
                            if (!first)
                                swift.Append(", ");

                            paramType.GenerateValuesAndHash(TextWriter.Null, swiftWriter, ref hasher, reverseRand);
                            first = false;
                        }
                        swift.AppendLine(")");
                        swift.AppendLine("}");
                        swift.AppendLine("");
                    }
                    else
                    {
                        swift.Append($"public func {swiftFuncNamePrefix}{i}(");

                        swift.AppendJoin(", ", funcParamTypes.Last().Select((t, i) => $"a{i}: {t.GenerateSwiftUse()}"));

                        swift.AppendLine(") -> Int {");
                        swift.AppendLine("    var hasher = HasherFNV1a()");
                        for (int j = 0; j < funcParamTypes.Last().Count; j++)
                        {
                            funcParamTypes.Last()[j].GenerateSwiftHashCombine(swift, $"a{j}");
                        }
                        swift.AppendLine("    return hasher.finalize()");
                        swift.AppendLine("}");
                        swift.AppendLine("");
                    }
                }
            }
            else
            {
                for (int i = 0; i < numFunctions; i++)
                {
                    int numFields = rand.Next(1, maxParametersOrFields + 1);
                    retTypes.Add(GenStructType(rand, numFields, $"S{i}"));
                    funcParamTypes.Add([]);

                    retTypes.Last().GenerateSwift(swift);
                    swift.AppendLine($"public func {swiftFuncNamePrefix}{i}() -> S{i} {{");
                    swift.Append("    return ");
                    Fnv1aHasher hasher = new();
                    Random retTestRand = CreateRandForTest(i);
                    retTypes.Last().GenerateValuesAndHash(TextWriter.Null, swiftWriter, ref hasher, retTestRand);
                    swift.AppendLine("");
                    swift.AppendLine("}");
                    swift.AppendLine("");
                }
            }

            File.WriteAllText($"/Users/jakobbotsch/dev/dotnet/runtime/src/tests/Interop/Swift/{testName}/{testName}.swift", swift.ToString());

            if (genLoweringTests)
            {
                string[] swiftcArgs = ["-g", "-o", "output.s", "-emit-assembly", "-Xllvm", "--x86-asm-syntax=intel", "-S", "-emit-ir", "-enable-library-evolution", $"{testName}.swift"];
                Invoke("swiftc", $"/Users/jakobbotsch/dev/dotnet/runtime/src/tests/Interop/Swift/{testName}", swiftcArgs, false, null);

                var entries = new List<(string MangledName, string Params)>();
                string ir = File.ReadAllText($"/Users/jakobbotsch/dev/dotnet/runtime/src/tests/Interop/Swift/{testName}/output.s");
                List<string[]?> swiftLowerings = new();
                int numMangledNamesFound = 0;
                foreach (Match match in Regex.Matches(ir, "swiftcc i64 @\"(.*)\"\\((.*)\\)"))
                {
                    string mangledName = match.Groups[1].Value;
                    if (mangledName.Contains($"{mangledNamePrefix}{numMangledNamesFound}"))
                    {
                        string swiftLoweringParams = match.Groups[2].Value;
                        if (swiftLoweringParams.Contains("dereferenceable"))
                        {
                            swiftLowerings.Add(null);
                        }
                        else
                        {
                            string[] swiftLowering = swiftLoweringParams.Split(',').Select(s => s.Trim()).Select(s => s.Split(' ')[0]).ToArray();
                            swiftLowerings.Add(swiftLowering);
                        }

                        numMangledNamesFound++;
                    }
                }

                StringBuilder csharp = new StringBuilder();
                Dictionary<string, string> llvmToLowered = new Dictionary<string, string>
                {
                    ["i8"] = "ExpectedLoweringAttribute.Lowered.Int8",
                    ["i16"] = "ExpectedLoweringAttribute.Lowered.Int16",
                    ["i32"] = "ExpectedLoweringAttribute.Lowered.Int32",
                    ["i64"] = "ExpectedLoweringAttribute.Lowered.Int64",
                    ["float"] = "ExpectedLoweringAttribute.Lowered.Float",
                    ["double"] = "ExpectedLoweringAttribute.Lowered.Double",
                };

                for (int i = 0; i < numFunctions; i++)
                {
                    string[]? swiftLowering = swiftLowerings[i];
                    if (swiftLowering == null)
                    {
                        string expectedLoweringAttribute = "[ExpectedLowering] // By reference";
                        funcParamTypes[i][0].GenerateCSharp(csharp, expectedLoweringAttribute, false);
                    }
                    else
                    {
                        string expectedLoweringAttribute =
                            "[ExpectedLowering(" +
                            string.Join(", ", swiftLowering.Select(t => llvmToLowered[t])) +
                            ")]";

                        funcParamTypes[i][0].GenerateCSharp(csharp, expectedLoweringAttribute, false);
                    }
                }

                File.WriteAllText($"/Users/jakobbotsch/dev/dotnet/runtime/src/tests/Interop/Swift/{testName}/{testName}.cs", csharp.ToString());
            }
            else
            {
                Invoke("bash", "/Users/jakobbotsch/dev/dotnet/runtime", ["src/tests/build.sh", "-tree:Interop/Swift", "-checked"], true, null);
                Console.WriteLine("---------- Getting mangled names -----------");
                string nmOutput = Invoke("nm", $"/Users/jakobbotsch/dev/dotnet/runtime/artifacts/tests/coreclr/osx.arm64.Checked/Interop/Swift/{testName}/{testName}", ["-gU", $"lib{testName}.dylib"], true, null);

                List<string> mangledNames = new();
                int numMangledNamesFound = 0;
                foreach (string line in nmOutput.ReplaceLineEndings().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
                {
                    string mangledName = line.Split(' ')[2];
                    mangledName = mangledName.TrimStart('_');
                    if (mangledName.Contains($"{mangledNamePrefix}{numMangledNamesFound}"))
                    {
                        mangledNames.Add(mangledName);
                        numMangledNamesFound++;
                    }
                }

                Console.WriteLine("Found {0} mangled names", numMangledNamesFound);
                Trace.Assert(mangledNames.Count == numFunctions);

                StringBuilder csharp = new();
                StringWriter csharpWriter = new StringWriter(csharp);

                if (genReversePinvoke)
                {
                    csharp.AppendLine("#pragma warning disable CS8500");
                    csharp.AppendLine("");
                }

                csharp.AppendLine("using System;");
                csharp.AppendLine("using System.Runtime.CompilerServices;");
                if (genReversePinvoke)
                {
                    csharp.AppendLine("using System.Runtime.ExceptionServices;");
                }
                csharp.AppendLine("using System.Runtime.InteropServices;");
                csharp.AppendLine("using System.Runtime.InteropServices.Swift;");
                csharp.AppendLine("using Xunit;");
                csharp.AppendLine("");
                csharp.AppendLine($"public unsafe class {testName}");
                csharp.AppendLine("{");
                csharp.AppendLine($"    private const string SwiftLib = \"lib{testName}.dylib\";");
                csharp.AppendLine("");

                for (int i = 0; i < numFunctions; i++)
                {
                    List<InteropType> paramTypes = funcParamTypes[i];
                    InteropType retType = retTypes[i];
                    string mangledName = mangledNames[i];

                    if (genParamTests || genReversePinvoke)
                    {
                        foreach (InteropType paramType in paramTypes)
                        {
                            paramType.GenerateCSharp(csharp, "", genReturnTests);
                        }
                    }

                    if (genReturnTests || genReversePinvoke)
                    {
                        retTypes[i].GenerateCSharp(csharp, "", true);
                    }

                    csharp.AppendLine("    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                    csharp.AppendLine($"    [DllImport(SwiftLib, EntryPoint = \"{mangledName}\")]");
                    if (genParamTests)
                    {
                        csharp.Append($"    private static extern nint {csharpFuncNamePrefix}{i}(");
                        csharp.AppendJoin(", ", paramTypes.Select((t, i) => $"{t.GenerateCSharpUse()} a{i}"));
                        csharp.AppendLine(");");
                        csharp.AppendLine("");
                        csharp.AppendLine("    [Fact]");
                        csharp.AppendLine($"    public static void Test{csharpFuncNamePrefix}{i}()");
                        csharp.AppendLine("    {");

                        Fnv1aHasher expectedHash = new();
                        csharp.AppendLine($"        Console.Write(\"Running {csharpFuncNamePrefix}{i}: \");");
                        csharp.Append($"        long result = {csharpFuncNamePrefix}{i}(");
                        for (int j = 0; j < paramTypes.Count; j++)
                        {
                            if (j > 0)
                                csharp.Append(", ");

                            paramTypes[j].GenerateValuesAndHash(csharpWriter, TextWriter.Null, ref expectedHash, rand);
                        }

                        csharp.AppendLine(");");
                        csharp.AppendLine($"        Assert.Equal({expectedHash.Finalize()}, result);");
                        csharp.AppendLine($"        Console.WriteLine(\"OK\");");
                        csharp.AppendLine("    }");
                        csharp.AppendLine("");
                    }
                    else if (genReversePinvoke)
                    {
                        csharp.Append($"    private static extern {retType.GenerateCSharpUse()} {csharpFuncNamePrefix}{i}(delegate* unmanaged[Swift]<");
                        csharp.AppendJoin(", ", paramTypes.Select((t, i) => t.GenerateCSharpUse()));
                        if (paramTypes.Count > 0)
                            csharp.Append(", ");
                        csharp.Append("SwiftSelf, ");
                        csharp.Append(retType.GenerateCSharpUse());
                        csharp.AppendLine("> f, SwiftSelf self);");
                        csharp.AppendLine("");
                        csharp.AppendLine("    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvSwift)])]");
                        csharp.Append($"    private static {retType.GenerateCSharpUse()} {csharpFuncNamePrefix}{i}Callback(");
                        csharp.AppendJoin(", ", paramTypes.Select((t, i) => $"{t.GenerateCSharpUse()} a{i}"));
                        if (paramTypes.Count > 0)
                            csharp.Append(", ");
                        csharp.AppendLine("SwiftSelf self)");
                        csharp.AppendLine("    {");
                        csharp.AppendLine("        try");
                        csharp.AppendLine("        {");
                        Random callbackRand = CreateRandForTest(i);
                        for (int j = 0; j < paramTypes.Count; j++)
                        {
                            paramTypes[j].GenerateAssertEqual(csharpWriter, $"a{j}", callbackRand, "            ");
                        }
                        csharp.AppendLine("        }");
                        csharp.AppendLine("        catch (Exception ex)");
                        csharp.AppendLine("        {");
                        csharp.AppendLine("            *(ExceptionDispatchInfo*)self.Value = ExceptionDispatchInfo.Capture(ex);");
                        csharp.AppendLine("        }");
                        csharp.AppendLine("");

                        csharp.Append("        return ");
                        var hasher = new Fnv1aHasher();
                        retType.GenerateValuesAndHash(csharpWriter, TextWriter.Null, ref hasher, callbackRand);
                        csharp.AppendLine(";");
                        csharp.AppendLine("    }");
                        csharp.AppendLine("");

                        csharp.AppendLine("    [Fact]");
                        csharp.AppendLine($"    public static void Test{csharpFuncNamePrefix}{i}()");
                        csharp.AppendLine("    {");
                        csharp.AppendLine($"        Console.Write(\"Running {csharpFuncNamePrefix}{i}: \");");
                        csharp.AppendLine($"        ExceptionDispatchInfo ex = null;");
                        csharp.AppendLine($"        {retType.GenerateCSharpUse()} val = {csharpFuncNamePrefix}{i}(&{csharpFuncNamePrefix}{i}Callback, new SwiftSelf(&ex));");
                        csharp.AppendLine($"        if (ex != null)");
                        csharp.AppendLine($"            ex.Throw();");
                        csharp.AppendLine($"");

                        callbackRand = CreateRandForTest(i);
                        for (int j = 0; j < paramTypes.Count; j++)
                        {
                            paramTypes[j].GenerateValuesAndHash(TextWriter.Null, TextWriter.Null, ref hasher, callbackRand);
                        }

                        retType.GenerateAssertEqual(csharpWriter, "val", callbackRand, "        ");
                        csharp.AppendLine($"        Console.WriteLine(\"OK\");");
                        csharp.AppendLine("    }");
                        csharp.AppendLine("");
                    }
                    else
                    {
                        csharp.AppendLine($"    private static extern {retType.GenerateCSharpUse()} {csharpFuncNamePrefix}{i}();");
                        csharp.AppendLine("");
                        csharp.AppendLine("    [Fact]");
                        csharp.AppendLine($"    public static void Test{csharpFuncNamePrefix}{i}()");
                        csharp.AppendLine("    {");
                        csharp.AppendLine($"        Console.Write(\"Running {csharpFuncNamePrefix}{i}: \");");
                        csharp.AppendLine($"        {retType.GenerateCSharpUse()} val = {csharpFuncNamePrefix}{i}();");
                        Random retTestRand = CreateRandForTest(i);
                        retType.GenerateAssertEqual(csharpWriter, "val", retTestRand, "        ");
                        csharp.AppendLine($"        Console.WriteLine(\"OK\");");
                        csharp.AppendLine("    }");
                        csharp.AppendLine("");
                    }
                }

                csharp.AppendLine("}");
                File.WriteAllText($"/Users/jakobbotsch/dev/dotnet/runtime/src/tests/Interop/Swift/{testName}/{testName}.cs", csharp.ToString());

                Invoke("bash", "/Users/jakobbotsch/dev/dotnet/runtime", ["src/tests/build.sh", "-tree:Interop/Swift", "-checked"], true, null);
            }
        }

        private static Random CreateRandForTest(int index)
        {
            Random retTestRand = new Random(unchecked((int)0xdeadbeef * index + 0x1234));
            return retTestRand;
        }

        private static readonly PrimitiveInteropType[] s_primitiveInteropTypes = [
            new PrimitiveInteropType("Int", "nint", 8),
            new PrimitiveInteropType("UInt", "nuint", 8),
            new PrimitiveInteropType("Int8", "sbyte", 1),
            new PrimitiveInteropType("Int16", "short", 2),
            new PrimitiveInteropType("Int32", "int", 4),
            new PrimitiveInteropType("Int64", "long", 8),
            new PrimitiveInteropType("UInt8", "byte", 1),
            new PrimitiveInteropType("UInt16", "ushort", 2),
            new PrimitiveInteropType("UInt32", "uint", 4),
            new PrimitiveInteropType("UInt64", "ulong", 8),
            new PrimitiveInteropType("Float", "float", 4),
            new PrimitiveInteropType("Double", "double", 8)
        ];

        private static List<InteropType> GenParameterTypes(Random rand, int numParams, string newTypePrefix)
        {
            List<InteropType> paramTypes = new();
            int numStructTypesGenerated = 0;
            for (int i = 0; i < numParams;)
            {
                InteropType type;
                // 40% chance of generating a top-level struct with up to 6 fields
                if (rand.NextDouble() < 0.4)
                {
                    int numFields = rand.Next(1, Math.Min(numParams - i, 6));
                    type = GenStructType(rand, numFields, $"{newTypePrefix}_S{numStructTypesGenerated++}");
                    i += numFields;
                }
                else
                {
                    type = s_primitiveInteropTypes[rand.Next(s_primitiveInteropTypes.Length)];
                    i++;
                }

                paramTypes.Add(type);
            }

            return paramTypes;
        }

        private static StructInteropType GenStructType(Random rand, int numRecursiveFields, string name)
        {
            List<InteropType> fields = new();
            int numStructTypesGenerated = 0;
            for (int i = 0; i < numRecursiveFields;)
            {
                InteropType type;
                // 10% chance of nested struct type
                if (rand.NextDouble() < 0.1)
                {
                    int numFields = rand.Next(1, numRecursiveFields - i);
                    type = GenStructType(rand, numFields, $"{name}_S{numStructTypesGenerated++}");
                    i += numFields;
                }
                else
                {
                    type = s_primitiveInteropTypes[rand.Next(s_primitiveInteropTypes.Length)];
                    i++;
                }

                fields.Add(type);
            }

            return new StructInteropType(name, fields.ToArray());
        }

        private static string Invoke(string fileName, string workingDir, string[] args, bool printOutput, string? outputPath, Func<int, bool>? checkExitCode = null)
        {
            var psi = new ProcessStartInfo(fileName)
            {
                FileName = fileName,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in args)
                psi.ArgumentList.Add(a);

            string command = fileName + " " + string.Join(" ", args.Select(a => "\"" + a + "\""));

            using Process? p = Process.Start(psi);
            if (p == null)
                throw new Exception("Could not start child process " + fileName);

            StringBuilder stdout = new();
            StringBuilder stderr = new();
            p.OutputDataReceived += (sender, args) =>
            {
                if (printOutput)
                {
                    Console.WriteLine(args.Data);
                }
                stdout.AppendLine(args.Data);
            };
            p.ErrorDataReceived += (sender, args) =>
            {
                if (printOutput)
                {
                    Console.Error.WriteLine(args.Data);
                }
                stderr.AppendLine(args.Data);
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();

            if (outputPath != null)
            {
                string all = command + Environment.NewLine + Environment.NewLine + "STDOUT:" + Environment.NewLine + stdout + Environment.NewLine + Environment.NewLine + "STDERR:" + Environment.NewLine + stderr;
                File.AppendAllText(outputPath, all);
            }

            if (checkExitCode == null ? p.ExitCode != 0 : !checkExitCode(p.ExitCode))
            {
                throw new Exception(
                    $@"
Child process '{fileName}' exited with error code {p.ExitCode}
stdout:
{stdout.ToString().Trim()}

stderr:
{stderr}".Trim());
            }


            return stdout.ToString();
        }

        private struct Fnv1aHasher
        {
            private ulong _hash = 14_695_981_039_346_656_037;
            private const ulong Prime = 1_099_511_628_211;

            public Fnv1aHasher()
            {
            }

            public void Combine<T>(T val) where T : unmanaged
            {
                ReadOnlySpan<byte> bytes = MemoryMarshal.Cast<T, byte>(MemoryMarshal.CreateSpan(ref val, 1));
                foreach (byte b in bytes)
                {
                    _hash ^= b;
                    _hash *= Prime;
                }
            }

            public long Finalize() => (long)_hash;
        }

        private abstract class InteropType
        {
            public abstract void GenerateSwift(StringBuilder sb);
            public abstract void GenerateCSharp(StringBuilder sb, string extraAttribute, bool ctor);
            public abstract string GenerateSwiftUse();
            public abstract string GenerateCSharpUse();
            public abstract void GenerateValuesAndHash(TextWriter csharp, TextWriter swift, ref Fnv1aHasher hasher, Random rand);
            public abstract void GenerateSwiftHashCombine(StringBuilder sb, string valueName);
            public abstract void GenerateAssertEqual(TextWriter csharp, string actualIdentifier, Random rand, string indent);
            public abstract int Size { get; }
            public abstract int Alignment { get; }
        }

        private class PrimitiveInteropType : InteropType
        {
            public string SwiftType { get; }
            public string CSharpType { get; }
            public override int Size { get; }
            public override int Alignment => Size;

            public PrimitiveInteropType(string swiftType, string cSharpType, int size)
            {
                SwiftType = swiftType;
                CSharpType = cSharpType;
                Size = size;
            }

            public override void GenerateCSharp(StringBuilder sb, string extraAttribute, bool ctor)
            {
            }

            public override void GenerateSwift(StringBuilder sb)
            {
            }

            public override string GenerateCSharpUse() => CSharpType;
            public override string GenerateSwiftUse() => SwiftType;

            public override void GenerateValuesAndHash(TextWriter csharp, TextWriter swift, ref Fnv1aHasher hasher, Random rand)
            {
                switch (CSharpType)
                {
                    case "nint":
                        {
                            nint value = (nint)rand.NextInt64();
                            hasher.Combine(value);
                            csharp.Write($"unchecked((nint){value})");
                            swift.Write(value);
                            break;
                        }
                    case "nuint":
                        {
                            nuint value = (nuint)rand.NextInt64();
                            hasher.Combine(value);
                            csharp.Write($"unchecked((nuint){value})");
                            swift.Write(value);
                            break;
                        }
                    case "sbyte":
                        {
                            sbyte value = checked((sbyte)rand.Next(sbyte.MinValue, sbyte.MaxValue + 1));
                            hasher.Combine(value);
                            csharp.Write(value);
                            swift.Write(value);
                            break;
                        }
                    case "short":
                        {
                            short value = checked((short)rand.Next(short.MinValue, short.MaxValue + 1));
                            hasher.Combine(value);
                            csharp.Write(value);
                            swift.Write(value);
                            break;
                        }
                    case "int":
                        {
                            int value = rand.Next();
                            hasher.Combine(value);
                            csharp.Write(value);
                            swift.Write(value);
                            break;
                        }
                    case "long":
                        {
                            long value = rand.NextInt64();
                            hasher.Combine(value);
                            csharp.Write(value);
                            swift.Write(value);
                            break;
                        }
                    case "byte":
                        {
                            byte value = checked((byte)rand.Next(byte.MinValue, byte.MaxValue + 1));
                            hasher.Combine(value);
                            csharp.Write(value);
                            swift.Write(value);
                            break;
                        }
                    case "ushort":
                        {
                            ushort value = checked((ushort)rand.Next(ushort.MinValue, ushort.MaxValue + 1));
                            hasher.Combine(value);
                            csharp.Write(value);
                            swift.Write(value);
                            break;
                        }
                    case "uint":
                        {
                            uint value = (uint)rand.Next();
                            hasher.Combine(value);
                            csharp.Write(value);
                            swift.Write(value);
                            break;
                        }
                    case "ulong":
                        {
                            ulong value = (ulong)rand.NextInt64();
                            hasher.Combine(value);
                            csharp.Write(value);
                            swift.Write(value);
                            break;
                        }
                    case "float":
                        {
                            float value = rand.Next(1 << 23);
                            hasher.Combine(value);
                            csharp.Write(value);
                            swift.Write(value);
                            break;
                        }
                    case "double":
                        {
                            double value = rand.NextInt64(1L << 52);
                            hasher.Combine(value);
                            csharp.Write(value);
                            swift.Write(value);
                            break;
                        }
                    default:
                        throw new InvalidOperationException();
                }
            }

            public override void GenerateSwiftHashCombine(StringBuilder sb, string valueName)
            {
                sb.AppendLine($"    hasher.combine({valueName});");
            }

            public override void GenerateAssertEqual(TextWriter csharp, string actualIdentifier, Random rand, string indent)
            {
                csharp.Write($"{indent}Assert.Equal(({CSharpType})");
                Fnv1aHasher hasher = new();
                GenerateValuesAndHash(csharp, TextWriter.Null, ref hasher, rand);
                csharp.Write(", ");
                csharp.Write(actualIdentifier);
                csharp.WriteLine(");");
            }
        }

        private class StructInteropType : InteropType
        {
            private readonly string _name;
            private readonly InteropType[] _fields;

            public override int Size { get; }
            public override int Alignment { get; }

            public StructInteropType(string name, InteropType[] fields)
            {
                _name = name;
                _fields = fields;

                (Size, Alignment) = ComputeSizeAlignment();
            }

            public override void GenerateCSharp(StringBuilder sb, string extraAttribute, bool ctor)
            {
                foreach (InteropType field in _fields)
                {
                    field.GenerateCSharp(sb, "", ctor);
                }

                sb.AppendLine($"    [StructLayout(LayoutKind.Sequential, Size = {Size})]");
                if (!string.IsNullOrWhiteSpace(extraAttribute))
                {
                    sb.AppendLine($"    {extraAttribute}");
                }
                sb.AppendLine($"    struct {_name}");
                sb.AppendLine("    {");
                for (int i = 0; i < _fields.Length; i++)
                {
                    sb.AppendLine($"        public {_fields[i].GenerateCSharpUse()} F{i};");
                }

                if (ctor)
                {
                    sb.AppendLine("");
                    sb.Append($"        public {_name}(");
                    for (int i = 0; i < _fields.Length; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");

                        sb.Append($"{_fields[i].GenerateCSharpUse()} f{i}");
                    }
                    sb.AppendLine(")");
                    sb.AppendLine("        {");
                    for (int i = 0; i < _fields.Length; i++)
                    {
                        sb.AppendLine($"            F{i} = f{i};");
                    }
                    sb.AppendLine("        }");
                }

                sb.AppendLine("    }");
                sb.AppendLine("");
            }

            public override string GenerateCSharpUse() => _name;

            public override void GenerateSwift(StringBuilder sb)
            {
                foreach (InteropType field in _fields)
                {
                    field.GenerateSwift(sb);
                }

                sb.AppendLine("@frozen");
                sb.AppendLine($"public struct {_name}");
                sb.AppendLine("{");

                for (int i = 0; i < _fields.Length; i++)
                {
                    sb.AppendLine($"    public let f{i} : {_fields[i].GenerateSwiftUse()};");
                }
                sb.AppendLine("}");
                sb.AppendLine("");
            }

            public override string GenerateSwiftUse() => _name;

            public override void GenerateValuesAndHash(TextWriter csharp, TextWriter swift, ref Fnv1aHasher hasher, Random rand)
            {
                csharp.Write($"new {_name}(");
                swift.Write($"{_name}(");

                for (int i = 0; i < _fields.Length; i++)
                {
                    if (i > 0)
                    {
                        csharp.Write(", ");
                        swift.Write(", ");
                    }

                    swift.Write($"f{i}: ");

                    _fields[i].GenerateValuesAndHash(csharp, swift, ref hasher, rand);
                }

                csharp.Write(")");
                swift.Write(")");
            }

            public override void GenerateSwiftHashCombine(StringBuilder sb, string valueName)
            {
                for (int i = 0; i < _fields.Length; i++)
                {
                    _fields[i].GenerateSwiftHashCombine(sb, $"{valueName}.f{i}");
                }
            }

            public override void GenerateAssertEqual(TextWriter csharp, string actualIdentifier, Random rand, string indent)
            {
                for (int i = 0; i < _fields.Length; i++)
                {
                    _fields[i].GenerateAssertEqual(csharp, $"{actualIdentifier}.F{i}", rand, indent);
                }
            }

            private (int Size, int Alignment) ComputeSizeAlignment()
            {
                int size = 0;
                int alignment = 1;

                foreach (InteropType field in _fields)
                {
                    int fieldSize = field.Size;
                    int fieldAlignment = field.Alignment;
                    Trace.Assert(BitOperations.IsPow2(fieldAlignment));

                    size = (size + fieldAlignment - 1) & ~(fieldAlignment - 1);
                    size += fieldSize;
                    alignment = Math.Max(alignment, fieldAlignment);
                }

                return (size, alignment);
            }
        }
    }
}
