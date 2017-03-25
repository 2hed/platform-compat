﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Cci;
using Microsoft.Cci.Extensions;

namespace NotImplementedScanner
{
    internal sealed class PlatformNotSupportedAnalyzer
    {
        private readonly IPlatformNotSupportedReporter _reporter;

        public PlatformNotSupportedAnalyzer(IPlatformNotSupportedReporter reporter)
        {
            _reporter = reporter;
        }

        public void AnalyzeAssembly(IAssembly assembly)
        {
            foreach (var type in assembly.GetAllTypes())
                AnalyzeType(type);
        }

        private void AnalyzeType(INamedTypeDefinition type)
        {
            if (!type.IsVisibleOutsideAssembly())
                return;

            foreach (var item in type.Members)
                AnalyzeMember(item);
        }

        private void AnalyzeMember(ITypeDefinitionMember item)
        {
            if (!item.IsVisibleOutsideAssembly())
                return;

            var result = AnalyzePlatformNotSupported(item);
            _reporter.Report(result, item);
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
            return accessors.Select(a => AnalyzePlatformNotSupported(a.ResolvedMethod))
                            .Aggregate(ExceptionResult.DoesNotThrow, (c, o) => c.Combine(o));
        }

        private static ExceptionResult AnalyzePlatformNotSupported(IMethodDefinition method, int nestingLevel = 0)
        {
            const int maxNestingLevel = 3;

            if (method is Dummy || method.IsAbstract)
                return ExceptionResult.DoesNotThrow;

            foreach (var op in GetOperationsPreceedingThrow(method))
            {
                // throw new PlatformNotSupportedExeption(...)
                if (op.OperationCode == OperationCode.Newobj &&
                    op.Value is IMethodReference m &&
                    IsPlatformNotSupported(m))
                {
                    return ExceptionResult.ThrowsAt(nestingLevel);
                }

                // throw SomeFactoryForPlatformNotSupportedExeption(...);
                if (op.Value is IMethodReference r &&
                    IsFactoryForPlatformNotSupported(r))
                {
                    return ExceptionResult.ThrowsAt(nestingLevel);
                }
            }

            var result = ExceptionResult.DoesNotThrow;

            if (nestingLevel < maxNestingLevel)
            {
                foreach (var calledMethod in GetCalls(method))
                {
                    var nestedResult = AnalyzePlatformNotSupported(calledMethod.ResolvedMethod, nestingLevel + 1);
                    result = result.Combine(nestedResult);
                }
            }

            return result;
        }

        private static IEnumerable<IOperation> GetOperationsPreceedingThrow(IMethodDefinition method)
        {
            IOperation previous = null;

            foreach (var op in method.Body.Operations)
            {
                if (op.OperationCode == OperationCode.Nop)
                    continue;

                if (op.OperationCode == OperationCode.Throw && previous != null)
                    yield return previous;

                previous = op;
            }
        }

        private static IEnumerable<IMethodReference> GetCalls(IMethodDefinition method)
        {
            return method.Body.Operations.Where(o => o.OperationCode == OperationCode.Call ||
                                                     o.OperationCode == OperationCode.Callvirt)
                                         .Select(o => o.Value as IMethodReference)
                                         .Where(m => m != null);
        }

        private static bool IsPlatformNotSupported(IMethodReference constructorReference)
        {
            return constructorReference.ContainingType.FullName() == "System.PlatformNotSupportedException";
        }

        private static bool IsFactoryForPlatformNotSupported(IMethodReference reference)
        {
            if (reference.ResolvedMethod is Dummy || reference.ResolvedMethod.IsAbstract)
                return false;

            IMethodReference constructorReference = null;

            foreach (var op in reference.ResolvedMethod.Body.Operations)
            {
                switch (op.OperationCode)
                {
                    case OperationCode.Newobj:
                        constructorReference = op.Value as IMethodReference;
                        break;
                    case OperationCode.Ret:
                        if (constructorReference != null && IsPlatformNotSupported(constructorReference))
                            return true;
                        break;
                }
            }

            return false;
        }
    }
}
