using System.Management;
using ControlCenter.Contracts;

namespace ControlCenter.WindowsPolicy;

// Fixed read-only projections. No serials, UUIDs, user names, hostnames or command lines.
internal static class SystemInformation
{
    internal const string DeviceId = "system-info";
    internal static StandardDevice Read()
    {
        var components = new List<object>();
        var diagnostics = new List<string>();
        Query("cpu", "Win32_Processor", "Name,Manufacturer", row => new { kind = "cpu", name = Text(row, "Name"), manufacturer = Text(row, "Manufacturer") });
        Query("mainboard", "Win32_BaseBoard", "Product,Manufacturer", row => new { kind = "mainboard", name = Text(row, "Product"), manufacturer = Text(row, "Manufacturer") });
        Query("memory", "Win32_PhysicalMemory", "PartNumber,Manufacturer,Capacity", row => new { kind = "memory", name = Text(row, "PartNumber"), manufacturer = Text(row, "Manufacturer"), capacityBytes = Convert.ToUInt64(row["Capacity"] ?? 0) });
        Query("gpu", "Win32_VideoController", "Name", row => new { kind = "gpu", name = Text(row, "Name") });
        Query("storage", "Win32_DiskDrive", "Model,Size", row => new { kind = "storage", name = Text(row, "Model"), capacityBytes = Convert.ToUInt64(row["Size"] ?? 0) });
        Query("os", "Win32_OperatingSystem", "Caption,Version", row => new { kind = "os", name = Text(row, "Caption"), version = Text(row, "Version") });
        return new(DeviceId, "My computer", "system", ["system.info"], diagnostics.Count == 0 ? "read-only" : "partial",
            "Read-only Windows CIM component names; no serial numbers or machine identifiers.",
            Wire.Element(new { schemaVersion = 1, readOnly = true, components, diagnostics }));

        void Query(string kind, string table, string fields, Func<ManagementBaseObject, object> select)
        {
            try
            {
                using var search = new ManagementObjectSearcher(new ManagementScope("root\\CIMV2"), new ObjectQuery($"SELECT {fields} FROM {table}"),
                    new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = true });
                using var rows = search.Get();
                var count = 0;
                foreach (ManagementBaseObject row in rows)
                {
                    using (row) { if (count++ >= 16) break; components.Add(select(row)); }
                }
            }
            catch (Exception) { diagnostics.Add(kind + ": information unavailable"); }
        }
    }
    private static string Text(ManagementBaseObject row, string field)
    {
        var value = Convert.ToString(row[field])?.Trim() ?? "Unknown";
        return value.Length > 160 ? value[..160] : value;
    }
}
