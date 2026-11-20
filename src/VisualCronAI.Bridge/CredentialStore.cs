using System;
using System.Runtime.InteropServices;
using VisualCronAI.Contracts;

namespace VisualCronAI.Bridge
{
    /// <summary>
    /// Reads a generic credential from Windows Credential Manager (stored per Windows user via DPAPI).
    /// Create it with: <c>cmdkey /generic:VisualCronAI/DEV /user:svc_visualcron /pass</c> (prompts for the password).
    /// </summary>
    internal static class CredentialStore
    {
        internal sealed class StoredCredential
        {
            public string UserName = "";
            public string Password = "";
        }

        private const int CRED_TYPE_GENERIC = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = false)]
        private static extern void CredFree(IntPtr buffer);

        public static StoredCredential ReadGeneric(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                throw new BridgeException(BridgeErrorCodes.InvalidRequest, "CredentialTarget is required for CredentialManager auth.");

            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr ptr))
            {
                int err = Marshal.GetLastWin32Error();
                // Message deliberately contains only the target name, never any credential material.
                throw new BridgeException(BridgeErrorCodes.ConnectionFailed,
                    "Windows Credential Manager entry '" + target + "' was not found or is not readable (Win32 error " + err + ").");
            }
            try
            {
                var cred = (CREDENTIAL)Marshal.PtrToStructure(ptr, typeof(CREDENTIAL))!;
                var result = new StoredCredential
                {
                    UserName = cred.UserName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(cred.UserName) ?? "",
                    Password = cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0
                        ? ""
                        : Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2) ?? "",
                };
                if (result.UserName.Length == 0)
                    throw new BridgeException(BridgeErrorCodes.ConnectionFailed, "Credential '" + target + "' has no user name.");
                return result;
            }
            finally
            {
                CredFree(ptr);
            }
        }
    }
}
