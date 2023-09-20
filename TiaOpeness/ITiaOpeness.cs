using System;
using TiaOpeness.Model;

namespace TiaOpeness
{
    public interface ITiaOpeness
    {
        bool IsValidProjectFile(string filePath);

        bool IsValidProjectArchive(string filePath);

        void OpenTiaPortal();

        void CloseTiaPortal();

        bool OpenProject(string filePath);

        bool OpenArchivedProject(string filePath, string destinationDir);

        void CloseProject();

        DeviceItem GetPlcItemByName(String deviceName, String name);

        DeviceItem GetPlcItemByName(String name);

        void Compile(DeviceItem deviceItem);

        void Download(DeviceItem deviceItem);

        void GoOnline(DeviceItem deviceItem);

        void GoOffline(DeviceItem deviceItem);
    }
}
