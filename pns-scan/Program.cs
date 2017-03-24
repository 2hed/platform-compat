﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Cci;
using Microsoft.Cci.Extensions;

namespace NotImplementedScanner
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                var toolName = Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]);
                Console.Error.WriteLine($"Usage: {toolName} <directory-or-binary> <out-path>");
                return 1;
            }

            var inputPath = args[0];
            var outputPath = args[1];
            var isFile = File.Exists(inputPath);
            var isDirectory = Directory.Exists(inputPath);
            if (!isFile && !isDirectory)
            {
                Console.Error.WriteLine($"ERROR: '{inputPath}' must be a file or directory.");
                return 1;
            }

            try
            {
                Run(inputPath, outputPath);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                return 1;
            }
        }

        private static void Run(string inputPath, string outputPath)
        {
            using (var textWriter = new StreamWriter(outputPath))
            {
                textWriter.Write("DocId");
                textWriter.Write(",");
                textWriter.Write("Namespace");
                textWriter.Write(",");
                textWriter.Write("Type");
                textWriter.Write(",");
                textWriter.Write("Member");
                textWriter.Write(",");
                textWriter.Write("Nesting");
                textWriter.WriteLine();

                foreach (var assembly in LoadPaths(inputPath))
                    AnalyzeAssembly(textWriter, assembly);
            }
        }

        private static void AnalyzeAssembly(StreamWriter textWriter, IAssembly assembly)
        {
            foreach (var type in assembly.GetAllTypes())
                AnalyzeType(textWriter, type);
        }

        private static void AnalyzeType(StreamWriter textWriter, INamedTypeDefinition type)
        {
            if (!type.IsVisibleOutsideAssembly())
                return;

            foreach (var item in type.Members)
                AnalyzeMember(textWriter, item);
        }

        private static void AnalyzeMember(StreamWriter textWriter, ITypeDefinitionMember item)
        {
            if (!item.IsVisibleOutsideAssembly())
                return;

            var result = AnalyzePlatformNotSupported(item);

            if (result.Throws)
            {
                textWriter.WriteEscaped(item.DocId());
                textWriter.Write(",");
                textWriter.WriteEscaped(item.ContainingTypeDefinition.GetNamespaceName());
                textWriter.Write(",");
                textWriter.WriteEscaped(item.ContainingTypeDefinition.GetTypeName(false));
                textWriter.Write(",");
                textWriter.WriteEscaped(GetMemberSignature(item));
                textWriter.Write(",");
                textWriter.Write(result);
                textWriter.WriteLine();
            }
        }

        private static IEnumerable<IAssembly> LoadPaths(string input)
        {
            var inputPaths = HostEnvironment.SplitPaths(input);
            var filePaths = HostEnvironment.GetFilePaths(inputPaths, SearchOption.AllDirectories).ToArray();
            return HostEnvironment.LoadAssemblySet(filePaths).Distinct();
        }

        private static ExceptionResult AnalyzePlatformNotSupported(ITypeDefinitionMember item)
        {
            if (item is IMethodDefinition m)
            {
                if (m.IsPropertyOrEventAccessor())
                    return ExceptionResult.DoesNotThrow;

                return AnalyzePlatformNotSupported(m);
            }
            else if (item is IPropertyDefinition p)
            {
                return AnalyzePlatformNotSupported(p.Accessors);
            }
            else if (item is IEventDefinition e)
            {
                return AnalyzePlatformNotSupported(e.Accessors);
            }
            else if (item is IFieldDefinition || item is ITypeDefinition)
            {
                // Ignore
                return ExceptionResult.DoesNotThrow;
            }
            else
            {
                throw new NotImplementedException($"Unexpected type member: {item.FullName()} ({item.GetApiKind()})");
            }
        }

        private static ExceptionResult AnalyzePlatformNotSupported(IEnumerable<IMethodReference> accessors)
        {
            return accessors.Select(a => AnalyzePlatformNotSupported(a.ResolvedTypeDefinitionMember))
                            .Aggregate(ExceptionResult.DoesNotThrow, (c, o) => c.Combine(o));
        }

        private static ExceptionResult AnalyzePlatformNotSupported(IMethodDefinition method, int nestingLevel = 0)
        {
            const int maxNestingLevel = 3;

            if (method is Dummy || method.IsAbstract)
                return ExceptionResult.DoesNotThrow;

            IMethodReference constructorReference = null;

            var result = ExceptionResult.DoesNotThrow;

            foreach (var op in method.Body.Operations)
            {
                switch (op.OperationCode)
                {
                    case OperationCode.Newobj:
                        constructorReference = op.Value as IMethodReference;
                        break;
                    case OperationCode.Nop:
                        // Ignore
                        break;
                    case OperationCode.Throw:
                        if (constructorReference != null && IsPlatformNotSupported(constructorReference))
                        {
                            var newResult = ExceptionResult.ThrowsAt(nestingLevel);
                            result = result.Combine(newResult);
                        }
                        break;
                    case OperationCode.Call:
                        if (nestingLevel < maxNestingLevel &&
                            op.Value is IMethodReference m)
                        {
                            var nestedResult = AnalyzePlatformNotSupported(m.ResolvedMethod, nestingLevel + 1);
                            result = result.Combine(nestedResult);
                        }
                        break;
                    default:
                        constructorReference = null;
                        break;
                }
            }

            return result;
        }

        private static bool IsPlatformNotSupported(IMethodReference constructorReference)
        {
            return constructorReference.ContainingType.FullName() == "System.PlatformNotSupportedException";
        }

        private static string GetMemberSignature(ITypeDefinitionMember member)
        {
            if (member is IFieldDefinition)
                return member.Name.Value;

            var memberSignature = MemberHelper.GetMemberSignature(member, NameFormattingOptions.Signature |
                                                                          NameFormattingOptions.TypeParameters |
                                                                          NameFormattingOptions.ContractNullable |
                                                                          NameFormattingOptions.OmitContainingType |
                                                                          NameFormattingOptions.OmitContainingNamespace |
                                                                          NameFormattingOptions.PreserveSpecialNames);
            return memberSignature;
        }
    }
}
