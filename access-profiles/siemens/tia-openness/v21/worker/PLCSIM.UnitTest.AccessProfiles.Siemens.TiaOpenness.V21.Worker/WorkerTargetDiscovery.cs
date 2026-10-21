using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.Worker
{
    internal sealed class WorkerTargetDiscovery
    {
        private readonly ILogger logger;
        private readonly WorkerExceptionMapper exceptionMapper;

        public WorkerTargetDiscovery(ILogger logger, WorkerExceptionMapper exceptionMapper)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.exceptionMapper = exceptionMapper ?? throw new ArgumentNullException(nameof(exceptionMapper));
        }

        internal void WriteTargetProbe(StreamWriter writer, object project)
        {
            try
            {
                foreach (var target in ProbeTargetContexts(project).Select(context => context.Target))
                {
                    WorkerProtocol.WriteLine(
                        writer,
                        WorkerProtocol.CreateMessage("Target")
                        + "|id=" + WorkerProtocol.Escape(target.Id)
                        + "|displayName=" + WorkerProtocol.Escape(target.DisplayName)
                        + "|technicalPath=" + WorkerProtocol.Escape(target.TechnicalPath)
                        + "|deviceName=" + WorkerProtocol.Escape(target.DeviceName)
                        + "|itemName=" + WorkerProtocol.Escape(target.ItemName)
                        + "|cpuFamily=" + WorkerProtocol.Escape(target.CpuFamily)
                        + "|cpuType=" + WorkerProtocol.Escape(target.CpuType)
                        + "|typeIdentifier=" + WorkerProtocol.Escape(target.TypeIdentifier)
                        + "|classification=" + WorkerProtocol.Escape(target.Classification)
                        + "|hasPlcSoftware=" + WorkerProtocol.Escape(target.HasPlcSoftware ? "true" : "false")
                        + "|position=" + WorkerProtocol.Escape(target.Position));
                }

                WorkerProtocol.WriteLine(writer, WorkerProtocol.CreateMessage("EndTargets"));
            }
            catch (Exception ex)
            {
                exceptionMapper.WriteExceptionError(
                    writer,
                    ex,
                    "TiaOpenness.V21.Worker.TargetProbeFailed",
                    "discover project targets");
            }
        }


        internal IEnumerable<TiaTargetContext> ProbeTargetContexts(object project)
        {
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in ProbeProjectDevices(project, "<ungrouped>", "scope:ungrouped"))
            {
                if (seenIds.Add(target.Target.Id))
                {
                    yield return target;
                }
            }

            var ungroupedDevicesGroup = project.GetType().GetProperty("UngroupedDevicesGroup")?.GetValue(project, null);
            if (ungroupedDevicesGroup is not null)
            {
                foreach (var target in ProbeDeviceGroup(ungroupedDevicesGroup, "<ungrouped>", "scope:ungrouped"))
                {
                    if (seenIds.Add(target.Target.Id))
                    {
                        yield return target;
                    }
                }
            }

            foreach (var group in ReadEnumerableProperty(project, "DeviceGroups"))
            {
                foreach (var target in ProbeDeviceUserGroup(group, string.Empty, string.Empty))
                {
                    if (seenIds.Add(target.Target.Id))
                    {
                        yield return target;
                    }
                }
            }
        }


        internal IEnumerable<TiaTargetContext> ProbeProjectDevices(
            object project,
            string groupPath,
            string groupIdPath)
        {
            foreach (var device in ReadEnumerableProperty(project, "Devices"))
            {
                var deviceName = ReadStringProperty(device, "Name");
                var devicePath = CombinePath(groupPath, deviceName);
                var deviceIdPath = CombinePath(groupIdPath, "device:" + EscapePathSegment(deviceName));
                foreach (var target in ProbeDeviceItems(deviceName, device, device, devicePath, deviceIdPath))
                {
                    yield return target;
                }
            }
        }


        internal IEnumerable<TiaTargetContext> ProbeDeviceUserGroup(
            object group,
            string parentPath,
            string parentIdPath)
        {
            var groupName = ReadStringProperty(group, "Name");
            var groupPath = CombinePath(parentPath, groupName);
            var groupIdPath = CombinePath(parentIdPath, "group:" + EscapePathSegment(groupName));

            foreach (var target in ProbeDeviceGroup(group, groupPath, groupIdPath))
            {
                yield return target;
            }

            foreach (var child in ReadEnumerableProperty(group, "Groups"))
            {
                foreach (var target in ProbeDeviceUserGroup(child, groupPath, groupIdPath))
                {
                    yield return target;
                }
            }
        }


        internal IEnumerable<TiaTargetContext> ProbeDeviceGroup(
            object group,
            string groupPath,
            string groupIdPath)
        {
            foreach (var device in ReadEnumerableProperty(group, "Devices"))
            {
                var deviceName = ReadStringProperty(device, "Name");
                var devicePath = CombinePath(groupPath, deviceName);
                var deviceIdPath = CombinePath(groupIdPath, "device:" + EscapePathSegment(deviceName));
                foreach (var target in ProbeDeviceItems(deviceName, device, device, devicePath, deviceIdPath))
                {
                    yield return target;
                }
            }
        }


        internal IEnumerable<TiaTargetContext> ProbeDeviceItems(
            string deviceName,
            object device,
            object parent,
            string parentPath,
            string parentIdPath)
        {
            foreach (var item in ReadEnumerableProperty(parent, "DeviceItems"))
            {
                var itemName = ReadStringProperty(item, "Name");
                var position = ReadStringProperty(item, "PositionNumber");
                var segment = string.IsNullOrWhiteSpace(position)
                    ? itemName
                    : $"{position}:{itemName}";
                var path = CombinePath(parentPath, segment);
                var idSegment = string.IsNullOrWhiteSpace(position)
                    ? "item:" + EscapePathSegment(itemName)
                    : "item:" + EscapePathSegment(position) + ":" + EscapePathSegment(itemName);
                var idPath = CombinePath(parentIdPath, idSegment);
                var classification = ReadStringProperty(item, "Classification");
                var plcSoftware = TryGetPlcSoftware(item);
                if (plcSoftware is not null)
                {
                    yield return new TiaTargetContext(
                        new TiaTargetProbe(
                            idPath,
                            string.IsNullOrWhiteSpace(itemName) ? deviceName : itemName,
                            path,
                            deviceName,
                            itemName,
                            "S7-1500",
                            ReadCpuType(item, out var typeIdentifier),
                            typeIdentifier,
                            classification,
                            true,
                            position),
                        item,
                        plcSoftware,
                        device);
                }

                foreach (var child in ProbeDeviceItems(deviceName, device, item, path, idPath))
                {
                    yield return child;
                }
            }
        }


        internal IReadOnlyList<object> ReadEnumerableProperty(object instance, string propertyName)
        {
            var value = instance.GetType().GetProperty(propertyName)?.GetValue(instance, null);
            if (value is not IEnumerable enumerable)
            {
                return Array.Empty<object>();
            }

            return enumerable.Cast<object>().ToArray();
        }


        internal string ReadStringProperty(object instance, string propertyName)
        {
            try
            {
                var value = instance.GetType().GetProperty(propertyName)?.GetValue(instance, null);
                return value?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }


        internal string ReadCpuType(object item, out string typeIdentifier)
        {
            typeIdentifier = ReadAttribute(item, "TypeIdentifier");
            if (!string.IsNullOrWhiteSpace(typeIdentifier))
            {
                return typeIdentifier;
            }

            return ReadStringProperty(item, "Name");
        }


        internal string ReadAttribute(object instance, string name)
        {
            try
            {
                var method = instance.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                var value = method?.Invoke(instance, new object[] { name });
                return value?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }


        internal object? TryGetPlcSoftware(object item)
        {
            try
            {
                var getService = item.GetType().GetMethods()
                    .FirstOrDefault(method => method.Name == "GetService"
                        && method.IsGenericMethodDefinition
                        && method.GetParameters().Length == 0);
                if (getService is null)
                {
                    return null;
                }

                var softwareContainerType = ResolveType(item, "Siemens.Engineering.HW.Features.SoftwareContainer");
                var plcSoftwareType = ResolveType(item, "Siemens.Engineering.SW.PlcSoftware");
                if (softwareContainerType is null || plcSoftwareType is null)
                {
                    return null;
                }

                var softwareContainer = getService.MakeGenericMethod(softwareContainerType).Invoke(item, null);
                var software = softwareContainer?.GetType().GetProperty("Software")?.GetValue(softwareContainer, null);
                return software is not null && plcSoftwareType.IsInstanceOfType(software) ? software : null;
            }
            catch
            {
                return null;
            }
        }


        internal string CombinePath(string parent, string child)
        {
            if (string.IsNullOrWhiteSpace(parent))
            {
                return child;
            }

            if (string.IsNullOrWhiteSpace(child))
            {
                return parent;
            }

            return parent + "/" + child;
        }


        internal string EscapePathSegment(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }


        internal Type? ResolveType(object instance, string fullName)
        {
            return instance.GetType().Assembly.GetType(fullName, throwOnError: false)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                    .FirstOrDefault(type => type is not null);
        }

    }

    internal sealed class TiaTargetContext
    {
        public TiaTargetContext(
            TiaTargetProbe target,
            object deviceItem,
            object plcSoftware,
            object device)
        {
            Target = target;
            DeviceItem = deviceItem;
            PlcSoftware = plcSoftware;
            Device = device;
        }

        public TiaTargetProbe Target { get; }

        public object DeviceItem { get; }

        public object PlcSoftware { get; }

        public object Device { get; }
    }


    internal sealed class TiaTargetProbe
    {
        public TiaTargetProbe(
            string id,
            string displayName,
            string technicalPath,
            string deviceName,
            string itemName,
            string cpuFamily,
            string cpuType,
            string typeIdentifier,
            string classification,
            bool hasPlcSoftware,
            string position)
        {
            Id = id;
            DisplayName = displayName;
            TechnicalPath = technicalPath;
            DeviceName = deviceName;
            ItemName = itemName;
            CpuFamily = cpuFamily;
            CpuType = cpuType;
            TypeIdentifier = typeIdentifier;
            Classification = classification;
            HasPlcSoftware = hasPlcSoftware;
            Position = position;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string TechnicalPath { get; }

        public string DeviceName { get; }

        public string ItemName { get; }

        public string CpuFamily { get; }

        public string CpuType { get; }

        public string TypeIdentifier { get; }

        public string Classification { get; }

        public bool HasPlcSoftware { get; }

        public string Position { get; }
    }


}
