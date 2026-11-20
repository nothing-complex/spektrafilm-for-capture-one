using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

public static partial class StartupHook
{
    private static async Task TestNativeSessionIsolation(Assembly assembly, object manager, MethodInfo open, Type access,
        object catalog, Forms.Form catalogForm, object panel, object viewer, string lookPath, byte[] saved, string sessionPath)
    {
        var sessionDirectory = Path.Combine(folder, "Session");
        Check(Path.GetDirectoryName(sessionPath) == sessionDirectory && Path.GetExtension(sessionPath) == ".cosessiondb"
            && !Directory.Exists(sessionDirectory), "session fixture is a new private directory");
        Directory.CreateDirectory(sessionDirectory);
        var hostProfile = assembly.GetType("HostProfile", true)!;
        var catalogRaw = (string)Prop(hostProfile.GetMethod("CaptureTarget", Flags)!.Invoke(null, new object[] { catalogForm })!, "SourcePath")!;
        string Digest(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        var sourceHash = Digest(catalogRaw);
        var dialogType = open.GetParameters()[3].ParameterType;
        var dialog = Activator.CreateInstance(dialogType, nonPublic: true)!;
        void Set(string name, object value) => dialogType.GetField(name, Flags)!.SetValue(dialog, value);
        Set("Path", sessionPath);
        Set("DocumentType", Enum.Parse(dialogType.GetField("DocumentType", Flags)!.FieldType, "Session"));
        Set("CaptureDirectoryName", "Capture");
        Set("SelectsDirectoryName", "Selects");
        Set("OutputDirectoryName", "Output");
        Set("TrashDirectoryName", "Trash");
        Set("CaptureNaming", "Fixture");
        Set("ShareFolderOnline", false);
        object[] Sessions() => ((IEnumerable)Call(manager, "GetOpenSessionDocuments")!).Cast<object>().ToArray();
        bool Matches(object document) => string.Equals((string)Prop(document, "Path")!, sessionPath, StringComparison.OrdinalIgnoreCase);
        Forms.Form? MainForm(object document) => (Forms.Form?)access.GetMethod("MainForm", Flags)!.Invoke(null, new[] { document });
        async Task WaitFor(Func<bool> predicate, string message)
        {
            var started = Stopwatch.GetTimestamp();
            while (!predicate())
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(20)) throw new TimeoutException(message);
                await Task.Delay(75);
            }
        }
        try
        {
            var result = open.Invoke(null, new[] { sessionPath, null, (object)true, dialog, false, true, null });
            File.AppendAllText(Path.Combine(folder, "host-smoke.log"), $"Native session creation result {result}.{Environment.NewLine}");
            await WaitFor(() => Sessions().Any(Matches), "Capture One did not create the disposable session.");
            var session = Sessions().Single(Matches);
            await WaitFor(() => MainForm(session) is { IsHandleCreated: true }, "The native session did not get a separate form.");
            var sessionForm = MainForm(session)!;
            _ = Call(manager, "SetActiveDocument", session);
            sessionForm.Activate();
            await WaitFor(() => ReferenceEquals(Prop(manager, "ActiveDocument"), session), "The session did not become active.");
            var capturePath = Path.Combine(sessionDirectory, "Capture");
            Check(File.Exists(sessionPath) && Directory.Exists(capturePath) && !ReferenceEquals(sessionForm, catalogForm),
                "licensed Capture One created its real session database, Capture folder and separate form");
            Check(!new DirectoryInfo(capturePath).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "the native Capture directory is inside the disposable session");
            Check(Prop(viewer, "HasPainted") is false && Prop(panel, "IsSpektraExportActive") is false,
                "an unapproved session does not use the catalog's live film pixels or Export");
            var sessionInterface = session.GetType().GetInterface("P1.C1.Core.ModelEntities.ISessionDocument")!;
            var capture = sessionInterface.GetProperty("CaptureFolderCollection")!.GetValue(session)!;
            var core = sessionInterface.Assembly;
            var folderInterface = core.GetType("P1.C1.Core.ModelEntities.IFolderCollection", true)!;
            var folderInfo = folderInterface.GetProperty("Folder")!.GetValue(capture)!;
            sessionInterface.GetMethod("SelectFolder")!.Invoke(session, new[] { folderInfo, (object)0, null, true });
            using var contents = (IDisposable)core.GetType("P1.C1.Core.ModelEntities.ICollection", true)!
                .GetMethod("RequestPopulateContents")!.Invoke(capture, null)!;
            var list = core.GetType("P1.C1.Core.ModelEntities.ISimpleCollection", true)!.GetMethod("GetAllFilteredAndVisibleVariants")!;
            int Captures() => ((IEnumerable)list.Invoke(capture, null)!).Cast<object>().Count();
            var before = Captures();
            var rawDestination = Path.Combine(capturePath, "Capture arrival" + Path.GetExtension(catalogRaw));
            var incoming = Path.Combine(sessionDirectory, "incoming-raw.tmp");
            File.Copy(catalogRaw, incoming);
            File.Move(incoming, rawDestination);
            await WaitFor(() => Captures() > before, "The native session collection did not register the new Capture folder RAW.");
            Check(Digest(rawDestination) == sourceHash && Digest(catalogRaw) == sourceHash,
                "the real session observes a newly arrived RAW while both original and copied bytes remain intact");
            Check(Prop(viewer, "HasPainted") is false && Prop(panel, "IsSpektraExportActive") is false
                && File.ReadAllBytes(lookPath).SequenceEqual(saved) && Directory.EnumerateDirectories(Path.Combine(folder, "Variant looks")).Count() == 1,
                "a native session Capture folder arrival neither reads nor writes the approved catalog look");
        }
        finally
        {
            if (Sessions().Any(Matches)) Call(manager, "CloseDocument", sessionPath);
            catalogForm.BringToFront();
            catalogForm.Activate();
            _ = Call(manager, "SetActiveDocument", catalog);
        }
        await WaitFor(() => !Sessions().Any(Matches) && ReferenceEquals(Prop(manager, "ActiveDocument"), catalog)
            && Prop(viewer, "HasPainted") is true, "Closing the session did not restore the exact catalog viewer.");
        Check(File.ReadAllBytes(lookPath).SequenceEqual(saved), "session teardown leaves the original catalog film pixels and settings available");
    }
}
