using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace MentoHUST.Desktop
{
    public sealed class NetworkObservation
    {
        // Unknown is deliberately different from offline (NCSI can be disabled).
        public bool? LinkUp;
        public bool? Internet;
    }

    public static class WindowsNetworkStatus
    {
        public static NetworkObservation Read(string captureId)
        {
            var result = new NetworkObservation();
            if (string.IsNullOrEmpty(captureId)) return result;
            try {
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                    captureId.IndexOf(n.Id.Trim('{', '}'), StringComparison.OrdinalIgnoreCase) >= 0);
                // A removed, previously selected adapter is unavailable, not another NIC.
                if (nic == null) { result.LinkUp = false; return result; }
                result.LinkUp = nic.OperationalStatus == OperationalStatus.Unknown || nic.OperationalStatus == OperationalStatus.Testing ?
                    (bool?)null : nic.OperationalStatus == OperationalStatus.Up;
                Guid adapterId;
                if (result.LinkUp == true && Guid.TryParse(nic.Id, out adapterId))
                    result.Internet = ReadInternet(adapterId);
            } catch (NetworkInformationException) { }
            return result;
        }

        internal static bool? ReadInternet(Guid adapterId)
        {
            object manager = null; INetworkConnections connections = null;
            try {
                manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")));
                connections = ((INetworkManager)manager).GetNetworkConnections();
                bool? found = null;
                while (true) {
                    INetworkConnection connection = null; uint fetched;
                    try {
                        int status = connections.Next(1, out connection, out fetched);
                        if (status < 0) Marshal.ThrowExceptionForHR(status);
                        if (fetched == 0 || connection == null) break;
                        if (connection.GetAdapterId() == adapterId) {
                            bool internet = (connection.GetConnectivity() & (0x40 | 0x400)) != 0;
                            found = found == true || internet;
                        }
                    } finally { Release(connection); }
                }
                return found;
            } catch (COMException) { return null; }
            catch (InvalidCastException) { return null; }
            finally { Release(connections); Release(manager); }
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        // These are the leading methods in the SDK's IDispatch vtables, in SDK order.
        // Explicit COM interfaces are needed: GUID return values fail with dynamic IDispatch.
        [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetworkManager
        {
            [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
            [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork(Guid id);
            INetworkConnections GetNetworkConnections();
        }
        [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetworkConnections
        {
            [return: MarshalAs(UnmanagedType.Interface)] object GetEnumerator();
            [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection connection, out uint fetched);
        }
        [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetworkConnection
        {
            [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork();
            [return: MarshalAs(UnmanagedType.VariantBool)] bool IsConnectedToInternet();
            [return: MarshalAs(UnmanagedType.VariantBool)] bool IsConnected();
            int GetConnectivity();
            Guid GetConnectionId();
            Guid GetAdapterId();
        }
    }
}
