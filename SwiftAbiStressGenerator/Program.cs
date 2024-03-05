﻿using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace SwiftAbiStressGenerator
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: SwiftAbiStressGenerator <num functions> <max parameters>");
                return;
            }

            int numFunctions = int.Parse(args[0]);
            int maxParameters = int.Parse(args[1]);

            Random rand = new Random(1234);

            StringBuilder swift = new();
            swift.AppendLine("import Foundation");
            swift.AppendLine("");
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

            List<List<InteropType>> paramTypes = new();

            for (int i = 0; i < numFunctions; i++)
            {
                int numParams = rand.Next(1, maxParameters + 1);
                paramTypes.Add(GenParameterTypes(rand, numParams, $"F{i}"));

                foreach (InteropType paramType in paramTypes.Last())
                {
                    paramType.GenerateSwift(swift);
                }

                swift.Append($"public func swiftFunc{i}(");

                swift.AppendJoin(", ", paramTypes.Last().Select((t, i) => $"a{i}: {t.GenerateSwiftUse()}"));

                swift.AppendLine(") -> Int {");
                swift.AppendLine("    var hasher = HasherFNV1a()");
                for (int j = 0; j < paramTypes.Last().Count; j++)
                {
                    paramTypes.Last()[j].GenerateSwiftHashCombine(swift, $"a{j}");
                }
                swift.AppendLine("    return hasher.finalize()");
                swift.AppendLine("}");
                swift.AppendLine("");
            }

            File.WriteAllText("/Users/jakobbotsch/dev/dotnet/runtime/src/tests/Interop/Swift/SwiftAbiStress/SwiftAbiStress.swift", swift.ToString());

            Invoke("bash", "/Users/jakobbotsch/dev/dotnet/runtime", ["src/tests/build.sh", "-tree:Interop/Swift", "-checked"], true, null);
            Console.WriteLine("---------- Getting mangled names -----------");
            string nmOutput = Invoke("nm", "/Users/jakobbotsch/dev/dotnet/runtime/artifacts/tests/coreclr/osx.arm64.Checked/Interop/Swift/SwiftAbiStress/SwiftAbiStress", ["-gU", "libSwiftAbiStress.dylib"], true, null);

            List<string> mangledNames = new();
            int numMangledNamesFound = 0;
            foreach (string line in nmOutput.ReplaceLineEndings().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                string mangledName = line.Split(' ')[2];
                mangledName = mangledName.TrimStart('_');
                if (mangledName.Contains($"swiftFunc{numMangledNamesFound}"))
                {
                    mangledNames.Add(mangledName);
                    numMangledNamesFound++;
                }
            }

            Console.WriteLine("Found {0} mangled names", numMangledNamesFound);
            Trace.Assert(mangledNames.Count == numFunctions && paramTypes.Count == numFunctions);

            StringBuilder csharp = new();
            csharp.AppendLine("using System;");
            csharp.AppendLine("using System.Runtime.CompilerServices;");
            csharp.AppendLine("using System.Runtime.InteropServices;");
            csharp.AppendLine("using System.Runtime.InteropServices.Swift;");
            csharp.AppendLine("using Xunit;");
            csharp.AppendLine("");
            csharp.AppendLine("public class SwiftAbiStress");
            csharp.AppendLine("{");
            csharp.AppendLine("    private const string SwiftLib = \"libSwiftAbiStress.dylib\";");
            csharp.AppendLine("");

            for (int i = 0; i < numFunctions; i++)
            {
                List<InteropType> types = paramTypes[i];
                string mangledName = mangledNames[i];

                foreach (InteropType paramType in types)
                {
                    paramType.GenerateCSharp(csharp);
                }

                csharp.AppendLine("    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                csharp.AppendLine($"    [DllImport(SwiftLib, EntryPoint = \"{mangledName}\")]");
                csharp.Append($"    private static extern nint SwiftFunc{i}(");
                csharp.AppendJoin(", ", types.Select((t, i) => $"{t.GenerateCSharpUse()} a{i}"));
                csharp.AppendLine(");");
                csharp.AppendLine("");
                csharp.AppendLine("    [Fact]");
                csharp.AppendLine($"    public static void TestSwiftFunc{i}()");
                csharp.AppendLine("    {");

                Fnv1aHasher expectedHash = new();
                csharp.AppendLine($"        Console.Write(\"Running SwiftFunc{i}: \");");
                csharp.Append($"        long result = SwiftFunc{i}(");
                for (int j = 0; j < types.Count; j++)
                {
                    if (j > 0)
                        csharp.Append(", ");

                    types[j].GenerateCSharpValueAndHash(csharp, ref expectedHash, rand);
                }

                csharp.AppendLine(");");
                csharp.AppendLine($"        Assert.Equal({expectedHash.Finalize()}, result);");
                csharp.AppendLine($"        Console.WriteLine(\"OK\");");
                csharp.AppendLine("    }");
                csharp.AppendLine("");
            }

            csharp.AppendLine("}");
            File.WriteAllText("/Users/jakobbotsch/dev/dotnet/runtime/src/tests/Interop/Swift/SwiftAbiStress/SwiftAbiStress.cs", csharp.ToString());

            Invoke("bash", "/Users/jakobbotsch/dev/dotnet/runtime", ["src/tests/build.sh", "-tree:Interop/Swift", "-checked"], true, null);
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
            public abstract void GenerateCSharp(StringBuilder sb);
            public abstract string GenerateSwiftUse();
            public abstract string GenerateCSharpUse();
            public abstract void GenerateCSharpValueAndHash(StringBuilder sb, ref Fnv1aHasher hasher, Random rand);
            public abstract void GenerateSwiftHashCombine(StringBuilder sb, string valueName);
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

            public override void GenerateCSharp(StringBuilder sb)
            {
            }

            public override void GenerateSwift(StringBuilder sb)
            {
            }

            public override string GenerateCSharpUse() => CSharpType;
            public override string GenerateSwiftUse() => SwiftType;

            public override void GenerateCSharpValueAndHash(StringBuilder sb, ref Fnv1aHasher hasher, Random rand)
            {
                switch (CSharpType)
                {
                    case "nint":
                        {
                            nint value = (nint)rand.NextInt64();
                            hasher.Combine(value);
                            sb.AppendFormat($"unchecked((nint){value})");
                            break;
                        }
                    case "nuint":
                        {
                            nuint value = (nuint)rand.NextInt64();
                            hasher.Combine(value);
                            sb.AppendFormat($"unchecked((nuint){value})");
                            break;
                        }
                    case "sbyte":
                        {
                            sbyte value = checked((sbyte)rand.Next(sbyte.MinValue, sbyte.MaxValue + 1));
                            hasher.Combine(value);
                            sb.Append(value);
                            break;
                        }
                    case "short":
                        {
                            short value = checked((short)rand.Next(short.MinValue, short.MaxValue + 1));
                            hasher.Combine(value);
                            sb.Append(value);
                            break;
                        }
                    case "int":
                        {
                            int value = rand.Next();
                            hasher.Combine(value);
                            sb.Append(value);
                            break;
                        }
                    case "long":
                        {
                            long value = rand.NextInt64();
                            hasher.Combine(value);
                            sb.Append(value);
                            break;
                        }
                    case "byte":
                        {
                            byte value = checked((byte)rand.Next(byte.MinValue, byte.MaxValue + 1));
                            hasher.Combine(value);
                            sb.Append(value);
                            break;
                        }
                    case "ushort":
                        {
                            ushort value = checked((ushort)rand.Next(ushort.MinValue, ushort.MaxValue + 1));
                            hasher.Combine(value);
                            sb.Append(value);
                            break;
                        }
                    case "uint":
                        {
                            uint value = (uint)rand.Next();
                            hasher.Combine(value);
                            sb.Append(value);
                            break;
                        }
                    case "ulong":
                        {
                            ulong value = (ulong)rand.NextInt64();
                            hasher.Combine(value);
                            sb.Append(value);
                            break;
                        }
                    case "float":
                        {
                            float value = rand.Next(1 << 23);
                            hasher.Combine(value);
                            sb.Append(value);
                            break;
                        }
                    case "double":
                        {
                            double value = rand.NextInt64(1L << 52);
                            hasher.Combine(value);
                            sb.Append(value);
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

            public override void GenerateCSharp(StringBuilder sb)
            {
                foreach (InteropType field in _fields)
                {
                    field.GenerateCSharp(sb);
                }

                sb.AppendLine($"    [StructLayout(LayoutKind.Sequential, Size = {Size})]");
                sb.AppendLine($"    struct {_name}");
                sb.AppendLine("    {");
                for (int i = 0; i < _fields.Length; i++)
                {
                    sb.AppendLine($"        public {_fields[i].GenerateCSharpUse()} F{i};");
                }

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

            public override void GenerateCSharpValueAndHash(StringBuilder sb, ref Fnv1aHasher hasher, Random rand)
            {
                sb.Append($"new {_name}(");

                for (int i = 0; i < _fields.Length; i++)
                {
                    if (i > 0)
                        sb.Append(", ");

                    _fields[i].GenerateCSharpValueAndHash(sb, ref hasher, rand);
                }

                sb.Append(")");
            }

            public override void GenerateSwiftHashCombine(StringBuilder sb, string valueName)
            {
                for (int i = 0; i < _fields.Length; i++)
                {
                    _fields[i].GenerateSwiftHashCombine(sb, $"{valueName}.f{i}");
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