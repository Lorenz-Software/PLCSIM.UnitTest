using System;
using System.Reflection;
namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6.Worker
{
    internal static partial class Program
    {
        private sealed class PlcCycleObservationAdapter
        {
            private const string DefaultModePropertyName = "IsSendSyncEventInDefaultModeEnabled";
            private const string RegisterMethodName = "RegisterOnSyncPointReachedEvent";
            private const string WaitMethodName = "WaitForOnSyncPointReachedEvent";
            private const string UnregisterMethodName = "UnregisterOnSyncPointReachedEvent";
            private readonly object instance;
            private readonly PropertyInfo defaultModeProperty;
            private readonly MethodInfo registerMethod;
            private readonly MethodInfo waitMethod;
            private readonly MethodInfo unregisterMethod;
            private readonly MemberInfo waitErrorCodeMember;
            private PlcCycleObservationAdapter(
                object instance,
                PropertyInfo defaultModeProperty,
                MethodInfo registerMethod,
                MethodInfo waitMethod,
                MethodInfo unregisterMethod,
                MemberInfo waitErrorCodeMember)
            {
                this.instance = instance;
                this.defaultModeProperty = defaultModeProperty;
                this.registerMethod = registerMethod;
                this.waitMethod = waitMethod;
                this.unregisterMethod = unregisterMethod;
                this.waitErrorCodeMember = waitErrorCodeMember;
            }
            public static bool TryCreate(
                object? instance,
                out PlcCycleObservationAdapter? adapter,
                out string failure)
            {
                adapter = null;
                failure = string.Empty;
                if (instance is null)
                {
                    failure = "No PLCSIM Advanced runtime is prepared.";
                    return false;
                }
                var type = instance.GetType();
                var defaultModeProperty = type.GetProperty(
                    DefaultModePropertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (defaultModeProperty is null
                    || defaultModeProperty.PropertyType != typeof(bool)
                    || !defaultModeProperty.CanRead
                    || !defaultModeProperty.CanWrite)
                {
                    failure = $"A readable and writable Boolean {DefaultModePropertyName} property was not found.";
                    return false;
                }
                var registerMethod = FindPublicInstanceMethod(type, RegisterMethodName, Type.EmptyTypes);
                if (registerMethod is null || registerMethod.ReturnType != typeof(void))
                {
                    failure = $"The required {RegisterMethodName}() method was not found.";
                    return false;
                }
                var waitMethod = FindPublicInstanceMethod(type, WaitMethodName, new[] { typeof(uint) });
                if (waitMethod is null)
                {
                    failure = $"The required {WaitMethodName}(uint) method was not found.";
                    return false;
                }
                var waitErrorCodeMember = FindPublicInstanceMember(waitMethod.ReturnType, "ErrorCode");
                if (waitErrorCodeMember is null)
                {
                    failure = $"The {WaitMethodName}(uint) result does not expose an ErrorCode member.";
                    return false;
                }
                var unregisterMethod = FindPublicInstanceMethod(type, UnregisterMethodName, Type.EmptyTypes);
                if (unregisterMethod is null || unregisterMethod.ReturnType != typeof(void))
                {
                    failure = $"The required {UnregisterMethodName}() method was not found.";
                    return false;
                }
                adapter = new PlcCycleObservationAdapter(
                    instance,
                    defaultModeProperty,
                    registerMethod,
                    waitMethod,
                    unregisterMethod,
                    waitErrorCodeMember);
                return true;
            }
            public RuntimeApiOperationResult SetDefaultModeEnabled(bool enabled)
            {
                try
                {
                    defaultModeProperty.SetValue(instance, enabled, null);
                    return RuntimeApiOperationResult.SucceededResult();
                }
                catch (TargetInvocationException ex)
                {
                    return Failed(ex);
                }
                catch (Exception ex)
                {
                    return Failed(ex);
                }
            }
            public RuntimeApiOperationResult Register()
            {
                return InvokeVoid(registerMethod);
            }
            public PlcCycleObservationWaitResult Wait(uint timeoutMs)
            {
                try
                {
                    var result = waitMethod.Invoke(instance, new object[] { timeoutMs });
                    if (result is null)
                    {
                        return PlcCycleObservationWaitResult.FromFailure(
                            $"{WaitMethodName}(uint) returned no result.");
                    }
                    var errorCode = ReadMemberValue(waitErrorCodeMember, result);
                    var errorCodeText = errorCode?.ToString() ?? string.Empty;
                    if (string.Equals(errorCodeText, "OK", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(errorCodeText, "0", StringComparison.OrdinalIgnoreCase))
                    {
                        return PlcCycleObservationWaitResult.Signaled();
                    }
                    if (string.Equals(errorCodeText, "Timeout", StringComparison.OrdinalIgnoreCase))
                    {
                        return PlcCycleObservationWaitResult.FromTimeout();
                    }
                    return PlcCycleObservationWaitResult.FromFailure(
                        string.IsNullOrWhiteSpace(errorCodeText)
                            ? $"{WaitMethodName}(uint) returned an empty error code."
                            : $"{WaitMethodName}(uint) failed with runtime error '{errorCodeText}'.",
                        errorCodeText);
                }
                catch (TargetInvocationException ex)
                {
                    return FailedWait(ex);
                }
                catch (Exception ex)
                {
                    return FailedWait(ex);
                }
            }
            public RuntimeApiOperationResult Unregister()
            {
                return InvokeVoid(unregisterMethod);
            }
            private RuntimeApiOperationResult InvokeVoid(MethodInfo method)
            {
                try
                {
                    method.Invoke(instance, null);
                    return RuntimeApiOperationResult.SucceededResult();
                }
                catch (TargetInvocationException ex)
                {
                    return Failed(ex);
                }
                catch (Exception ex)
                {
                    return Failed(ex);
                }
            }
            private static MethodInfo? FindPublicInstanceMethod(
                Type type,
                string name,
                Type[] parameterTypes)
            {
                return type.GetMethod(
                    name,
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null,
                    types: parameterTypes,
                    modifiers: null);
            }
            private static MemberInfo? FindPublicInstanceMember(Type type, string name)
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property is not null && property.CanRead)
                {
                    return property;
                }
                return type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            }
            private static object? ReadMemberValue(MemberInfo member, object instance)
            {
                if (member is PropertyInfo property)
                {
                    return property.GetValue(instance, null);
                }
                return ((FieldInfo)member).GetValue(instance);
            }
            private static RuntimeApiOperationResult Failed(Exception exception)
            {
                return RuntimeApiOperationResult.Failed(
                    exception.GetBaseException().Message,
                    RuntimeApiReflection.ReadRuntimeErrorCode(exception));
            }
            private static PlcCycleObservationWaitResult FailedWait(Exception exception)
            {
                return PlcCycleObservationWaitResult.FromFailure(
                    exception.GetBaseException().Message,
                    RuntimeApiReflection.ReadRuntimeErrorCode(exception));
            }
        }
        private sealed class PlcCycleObservationWaitResult
        {
            private PlcCycleObservationWaitResult(
                bool eventSignaled,
                bool timedOut,
                string message,
                string errorCode)
            {
                EventSignaled = eventSignaled;
                TimedOut = timedOut;
                Message = message;
                ErrorCode = errorCode;
            }
            public bool EventSignaled { get; }
            public bool TimedOut { get; }
            public bool Failed => !EventSignaled && !TimedOut;
            public string Message { get; }
            public string ErrorCode { get; }
            public static PlcCycleObservationWaitResult Signaled()
            {
                return new PlcCycleObservationWaitResult(true, false, string.Empty, string.Empty);
            }
            public static PlcCycleObservationWaitResult FromTimeout()
            {
                return new PlcCycleObservationWaitResult(false, true, string.Empty, "Timeout");
            }
            public static PlcCycleObservationWaitResult FromFailure(string message, string errorCode = "")
            {
                return new PlcCycleObservationWaitResult(false, false, message, errorCode);
            }
        }
    }
}
