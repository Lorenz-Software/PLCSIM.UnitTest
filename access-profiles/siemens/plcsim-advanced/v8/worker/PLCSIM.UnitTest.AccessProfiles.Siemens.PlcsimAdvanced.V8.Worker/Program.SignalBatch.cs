using System;
using System.Collections.Generic;
using System.Reflection;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker
{
    internal static partial class Program
    {
        private sealed partial class RuntimeApiContext
        {
            private SignalBatchReadResult ReadSignalBatch(IReadOnlyList<SignalReadRequest> requests)
            {
                if (preparedInstance is null)
                {
                    return SignalBatchReadResult.Failed("No PLCSIM Advanced runtime is prepared.");
                }

                var method = FindReadSignalsMethod(preparedInstance.GetType());
                if (method is null)
                {
                    return SignalBatchReadResult.Failed(
                        "ReadSignals(ref SDataValueByName[]) method was not found.");
                }

                var arrayType = method.GetParameters()[0].ParameterType.GetElementType();
                var elementType = arrayType?.GetElementType();
                if (elementType is null)
                {
                    return SignalBatchReadResult.Failed(
                        "ReadSignals signal element type could not be resolved.");
                }

                var signals = Array.CreateInstance(elementType, requests.Count);
                for (var index = 0; index < requests.Count; index++)
                {
                    var signal = Activator.CreateInstance(elementType);
                    if (signal is null || !TryWritePublicMember(signal, "Name", requests[index].Tag))
                    {
                        return SignalBatchReadResult.Failed(
                            $"ReadSignals signal name '{requests[index].Tag}' could not be initialized.");
                    }

                    signals.SetValue(signal, index);
                }

                var arguments = new object[] { signals };
                try
                {
                    method.Invoke(preparedInstance, arguments);
                }
                catch (TargetInvocationException ex)
                {
                    return SignalBatchReadResult.Failed(
                        ex.GetBaseException().Message,
                        RuntimeApiReflection.ReadRuntimeErrorCode(ex));
                }

                var returnedSignals = arguments[0] as Array ?? signals;
                var values = new ValueReadResult[requests.Count];
                for (var index = 0; index < requests.Count; index++)
                {
                    var signal = returnedSignals.GetValue(index);
                    if (signal is null)
                    {
                        return SignalBatchReadResult.Failed(
                            $"ReadSignals returned no value for '{requests[index].Tag}'.");
                    }

                    var errorCode = ReadPublicMember(signal, "ErrorCode");
                    if (!IsSuccessfulSignalErrorCode(errorCode))
                    {
                        var code = errorCode?.ToString() ?? string.Empty;
                        return SignalBatchReadResult.Failed(
                            $"ReadSignals failed for '{requests[index].Tag}' with error code '{code}'.",
                            code);
                    }

                    var dataValue = ReadPublicMember(signal, "DataValue");
                    var value = dataValue is null
                        ? null
                        : ReadPublicMember(dataValue, requests[index].ValueMember);
                    if (value is null)
                    {
                        return SignalBatchReadResult.Failed(
                            $"ReadSignals returned no '{requests[index].ValueMember}' value for '{requests[index].Tag}'.");
                    }

                    values[index] = ValueReadResult.Success(value);
                }

                return SignalBatchReadResult.Success(values);
            }

            private static MethodInfo? FindReadSignalsMethod(Type instanceType)
            {
                foreach (var method in instanceType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!string.Equals(method.Name, "ReadSignals", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parameters = method.GetParameters();
                    if (parameters.Length != 1
                        || !parameters[0].ParameterType.IsByRef
                        || !parameters[0].ParameterType.GetElementType()!.IsArray)
                    {
                        continue;
                    }

                    var arrayType = parameters[0].ParameterType.GetElementType();
                    var elementType = arrayType?.GetElementType();
                    if (elementType is not null
                        && string.Equals(elementType.Name, "SDataValueByName", StringComparison.Ordinal))
                    {
                        return method;
                    }
                }

                return null;
            }

            private static object? ReadPublicMember(object instance, string memberName)
            {
                var type = instance.GetType();
                return type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance, null)
                    ?? type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance);
            }

            private static bool TryWritePublicMember(object instance, string memberName, object value)
            {
                var type = instance.GetType();
                var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
                if (property is not null && property.CanWrite)
                {
                    property.SetValue(instance, value, null);
                    return true;
                }

                var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
                if (field is null)
                {
                    return false;
                }

                field.SetValue(instance, value);
                return true;
            }

            private static bool IsSuccessfulSignalErrorCode(object? errorCode)
            {
                if (errorCode is null)
                {
                    return false;
                }

                try
                {
                    return Convert.ToInt64(errorCode) == 0;
                }
                catch
                {
                    var text = errorCode.ToString() ?? string.Empty;
                    return string.Equals(text, "OK", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(text, "NoError", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(text, "Success", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private sealed class SignalReadRequest
        {
            public SignalReadRequest(string tag, string valueMember)
            {
                Tag = tag;
                ValueMember = valueMember;
            }

            public string Tag { get; }

            public string ValueMember { get; }
        }

        private sealed class SignalBatchReadResult
        {
            private SignalBatchReadResult(
                bool succeeded,
                IReadOnlyList<ValueReadResult> values,
                string message,
                string errorCode)
            {
                Succeeded = succeeded;
                Values = values;
                Message = message;
                ErrorCode = errorCode;
            }

            public bool Succeeded { get; }

            public IReadOnlyList<ValueReadResult> Values { get; }

            public string Message { get; }

            public string ErrorCode { get; }

            public static SignalBatchReadResult Success(IReadOnlyList<ValueReadResult> values)
            {
                return new SignalBatchReadResult(true, values, string.Empty, string.Empty);
            }

            public static SignalBatchReadResult Failed(string message, string errorCode = "")
            {
                return new SignalBatchReadResult(
                    false,
                    Array.Empty<ValueReadResult>(),
                    message,
                    errorCode);
            }
        }
    }
}
