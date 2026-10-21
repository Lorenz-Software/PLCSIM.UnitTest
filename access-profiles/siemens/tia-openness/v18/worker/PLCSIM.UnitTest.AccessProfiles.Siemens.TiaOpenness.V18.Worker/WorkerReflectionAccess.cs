using System;
using System.Collections.Generic;
using System.Linq;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18.Worker
{
    internal sealed class WorkerReflectionAccess
    {
        private readonly WorkerTargetDiscovery targetDiscovery;

        public WorkerReflectionAccess(WorkerTargetDiscovery targetDiscovery)
        {
            this.targetDiscovery = targetDiscovery ?? throw new ArgumentNullException(nameof(targetDiscovery));
        }

        internal object? ResolveService(object instance, string fullTypeName)
        {
            var getService = instance.GetType().GetMethods()
                .FirstOrDefault(method => method.Name == "GetService"
                    && method.IsGenericMethodDefinition
                    && method.GetParameters().Length == 0);
            if (getService is null)
            {
                return null;
            }

            var serviceType = targetDiscovery.ResolveType(instance, fullTypeName);
            if (serviceType is null)
            {
                return null;
            }

            return getService.MakeGenericMethod(serviceType).Invoke(instance, null);
        }


        internal IReadOnlyList<object> ReadOperationMessages(object operationResult, out bool truncated)
        {
            var messages = new List<object>();
            var pending = new Queue<object>(targetDiscovery.ReadEnumerableProperty(operationResult, "Messages"));
            while (pending.Count > 0 && messages.Count < WorkerProtocol.MaximumOperationMessageCount)
            {
                var message = pending.Dequeue();
                messages.Add(message);
                foreach (var child in targetDiscovery.ReadEnumerableProperty(message, "Messages"))
                {
                    pending.Enqueue(child);
                }
            }

            truncated = pending.Count > 0;
            return messages;
        }


        internal int ReadIntProperty(object instance, string propertyName)
        {
            try
            {
                var value = instance.GetType().GetProperty(propertyName)?.GetValue(instance, null);
                return value is int typed
                    ? typed
                    : int.TryParse(value?.ToString(), out var parsed) ? parsed : 0;
            }
            catch
            {
                return 0;
            }
        }


    }
}
