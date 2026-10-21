using System;
using System.Linq;
using System.Reflection;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker
{
    internal static partial class Program
    {
        private static class RuntimeApiReflection
        {
            public static object ResolveEnumValue(Type type, params string[] names)
            {
                foreach (var name in names)
                {
                    if (Enum.GetNames(type).Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return Enum.Parse(type, name, ignoreCase: true);
                    }
                }

                return Enum.GetValues(type).GetValue(0)
                    ?? throw new InvalidOperationException("Enum did not contain any values.");
            }

            public static void WriteProperty(Type type, object? instance, string propertyName, object value)
            {
                var property = type.GetProperty(propertyName, BindingFlags.Public | (instance is null ? BindingFlags.Static : BindingFlags.Instance));
                if (property is not null && property.CanWrite)
                {
                    property.SetValue(instance, value, null);
                }
            }

            public static RuntimeApiOperationResult TryWriteProperty(
                Type type,
                object? instance,
                string propertyName,
                object value)
            {
                var property = type.GetProperty(
                    propertyName,
                    BindingFlags.Public | (instance is null ? BindingFlags.Static : BindingFlags.Instance));
                if (property is null)
                {
                    return RuntimeApiOperationResult.Failed($"{propertyName} property was not found.");
                }

                if (!property.CanWrite)
                {
                    return RuntimeApiOperationResult.Failed($"{propertyName} property is read-only.");
                }

                try
                {
                    property.SetValue(instance, value, null);
                    return RuntimeApiOperationResult.SucceededResult();
                }
                catch (TargetInvocationException ex)
                {
                    return RuntimeApiOperationResult.Failed(
                        ex.GetBaseException().Message,
                        ReadRuntimeErrorCode(ex));
                }
                catch (Exception ex)
                {
                    return RuntimeApiOperationResult.Failed(
                        ex.Message,
                        ReadRuntimeErrorCode(ex));
                }
            }

            public static RuntimeApiOperationResult InvokeRuntimeOperation(
                object instance,
                string methodName,
                uint? timeoutMs = null)
            {
                var type = instance.GetType();
                var method = timeoutMs.HasValue
                    ? type.GetMethod(methodName, new[] { typeof(uint) })
                    : type.GetMethod(methodName, Type.EmptyTypes);
                method ??= type.GetMethod(methodName, Type.EmptyTypes);
                if (method is null)
                {
                    return RuntimeApiOperationResult.Failed($"{methodName} method was not found.");
                }

                var parameters = method.GetParameters().Length == 1
                    ? new object[] { timeoutMs.GetValueOrDefault() }
                    : Array.Empty<object>();
                try
                {
                    return MapRuntimeApiOperationResult(method.Invoke(instance, parameters));
                }
                catch (TargetInvocationException ex)
                {
                    return RuntimeApiOperationResult.Failed(
                        ex.GetBaseException().Message,
                        ReadRuntimeErrorCode(ex));
                }
                catch (Exception ex)
                {
                    return RuntimeApiOperationResult.Failed(
                        ex.Message,
                        ReadRuntimeErrorCode(ex));
                }
            }

            public static RuntimeApiOperationResult InvokeRuntimeOperation(
                object instance,
                string methodName,
                bool value)
            {
                var method = instance.GetType().GetMethod(methodName, new[] { typeof(bool) });
                if (method is null)
                {
                    return RuntimeApiOperationResult.Failed($"{methodName}(bool) method was not found.");
                }

                try
                {
                    return MapRuntimeApiOperationResult(method.Invoke(instance, new object[] { value }));
                }
                catch (TargetInvocationException ex)
                {
                    return RuntimeApiOperationResult.Failed(
                        ex.GetBaseException().Message,
                        ReadRuntimeErrorCode(ex));
                }
            }

            private static RuntimeApiOperationResult MapRuntimeApiOperationResult(object? result)
            {
                if (result is null)
                {
                    return RuntimeApiOperationResult.SucceededResult();
                }

                if (result is bool boolResult)
                {
                    return boolResult
                        ? RuntimeApiOperationResult.SucceededResult()
                        : RuntimeApiOperationResult.Failed("Operation returned false.");
                }

                var errorCode = ReadPublicMember(result, "ErrorCode");
                if (errorCode is not null)
                {
                    var codeText = errorCode.ToString() ?? string.Empty;
                    if (string.Equals(codeText, "0", StringComparison.OrdinalIgnoreCase)
                        || codeText.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0
                        || codeText.IndexOf("NoError", StringComparison.OrdinalIgnoreCase) >= 0
                        || codeText.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return RuntimeApiOperationResult.SucceededResult();
                    }

                    return RuntimeApiOperationResult.Failed(codeText, codeText);
                }

                var text = result.ToString() ?? string.Empty;
                return text.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0
                    ? RuntimeApiOperationResult.SucceededResult()
                    : RuntimeApiOperationResult.Failed(text);
            }

            private static object? ReadPublicMember(object instance, string memberName)
            {
                var type = instance.GetType();
                var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
                if (property is not null)
                {
                    return property.GetValue(instance, null);
                }

                var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
                return field?.GetValue(instance);
            }

            public static string ReadRuntimeErrorCode(Exception exception)
            {
                var errorCode = ReadPublicMember(exception.GetBaseException(), "RuntimeErrorCode");
                return errorCode?.ToString() ?? string.Empty;
            }
        }
    }
}
