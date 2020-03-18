using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

using Xamarin.Android.Tools;

using XATInfo   = Xamarin.Android.Tools.JdkInfo;

namespace Java.Interop.BootstrapTasks
{
	public class JdkInfo : Microsoft.Build.Utilities.Task
	{
		const string JARSIGNER = "jarsigner.exe";
		const string MDREG_KEY = @"SOFTWARE\Novell\Mono for Android";
		const string MDREG_JAVA_SDK = "JavaSdkDirectory";

		public  string  JdksRoot              { get; set; }

		public  string  MaximumJdkVersion     { get; set; }

		static  Regex   VersionExtractor  = new Regex (@"(?<version>[\d]+(\.\d+)+)", RegexOptions.Compiled);

		[Required]
		public  ITaskItem       PropertyFile        { get; set; }

		[Required]
		public  ITaskItem       MakeFragmentFile    { get; set; }

		[Output]
		public  string          JavaHomePath        { get; set; }

		public override bool Execute ()
		{
			var maxVersion      = GetVersion (MaximumJdkVersion);

			XATInfo jdk         = XATInfo.GetKnownSystemJdkInfos (CreateLogger ())
				.Where (j => maxVersion != null ? j.Version <= maxVersion : true)
				.Where (j => j.IncludePath.Any ())
				.FirstOrDefault ();

			if (jdk == null) {
				Log.LogError ("Could not determine JAVA_HOME location. Please set JdksRoot or export the JAVA_HOME environment variable.");
				return false;
			}

			JavaHomePath  = jdk.HomePath;

			Directory.CreateDirectory (Path.GetDirectoryName (PropertyFile.ItemSpec));
			Directory.CreateDirectory (Path.GetDirectoryName (MakeFragmentFile.ItemSpec));

			WritePropertyFile (jdk.JarPath, jdk.JavacPath, jdk.JdkJvmPath, jdk.IncludePath);
			WriteMakeFragmentFile (jdk.JarPath, jdk.JavacPath, jdk.JdkJvmPath, jdk.IncludePath);

			return !Log.HasLoggedErrors;
		}

		Version GetVersion (string value)
		{
			if (string.IsNullOrEmpty (value))
				return null;
			if (!value.Contains (".")) {
				value += ".0";
			}
			Version v;
			if (Version.TryParse (value, out v))
				return v;
			return null;
		}

		Action<TraceLevel, string> CreateLogger ()
		{
			Action<TraceLevel, string> logger = (level, value) => {
				switch (level) {
					case TraceLevel.Error:
						Log.LogError ("{0}", value);
						break;
					case TraceLevel.Warning:
						Log.LogWarning ("{0}", value);
						break;
					default:
						Log.LogMessage (MessageImportance.Low, "{0}", value);
						break;
				}
			};
			return logger;
		}

		void WritePropertyFile (string jarPath, string javacPath, string jdkJvmPath, IEnumerable<string> includes)
		{
			var msbuild = XNamespace.Get ("http://schemas.microsoft.com/developer/msbuild/2003");
			var project = new XElement (msbuild + "Project",
				new XElement (msbuild + "Choose",
					new XElement (msbuild + "When", new XAttribute ("Condition", " '$(JdkJvmPath)' == '' "),
						new XElement (msbuild + "PropertyGroup",
							new XElement (msbuild + "JdkJvmPath", jdkJvmPath)),
						new XElement (msbuild + "ItemGroup",
							includes.Select (i => new XElement (msbuild + "JdkIncludePath", new XAttribute ("Include", i)))))),
				new XElement (msbuild + "PropertyGroup",
					new XElement (msbuild + "JavaCPath", new XAttribute ("Condition", " '$(JavaCPath)' == '' "),
						javacPath),
					new XElement (msbuild + "JarPath", new XAttribute ("Condition", " '$(JarPath)' == '' "),
						jarPath)));
			project.Save (PropertyFile.ItemSpec);
		}

		void WriteMakeFragmentFile (string jarPath, string javacPath, string jdkJvmPath, IEnumerable<string> includes)
		{
			using (var o = new StreamWriter (MakeFragmentFile.ItemSpec)) {
				o.WriteLine ($"export  JI_JAR_PATH          := {jarPath}");
				o.WriteLine ($"export  JI_JAVAC_PATH        := {javacPath}");
				o.WriteLine ($"export  JI_JDK_INCLUDE_PATHS := {string.Join (" ", includes)}");
				o.WriteLine ($"export  JI_JVM_PATH          := {jdkJvmPath}");
			}
		}
	}
}

namespace Xamarin.Android.Tools
{
	internal class JdkInfo {

		public      string                              HomePath                    {get;}

		public      string                              Locator                     {get;}

		public      string                              JarPath                     {get;}
		public      string                              JavaPath                    {get;}
		public      string                              JavacPath                   {get;}
		public      string                              JdkJvmPath                  {get;}
		public      ReadOnlyCollection<string>          IncludePath                 {get;}

		public      Version                             Version                     => javaVersion.Value;
		public      string                              Vendor                      {
			get {
				if (GetJavaSettingsPropertyValue ("java.vendor", out string vendor))
					return vendor;
				return null;
			}
		}

		public      ReadOnlyDictionary<string, string>  ReleaseProperties           {get;}
		public      IEnumerable<string>                 JavaSettingsPropertyKeys    => javaProperties.Value.Keys;

		Lazy<Dictionary<string, List<string>>>      javaProperties;
		Lazy<Version>                               javaVersion;

		public JdkInfo (string homePath)
		{
			if (homePath == null)
				throw new ArgumentNullException (nameof (homePath));
			if (!Directory.Exists (homePath))
				throw new ArgumentException ("Not a directory", nameof (homePath));

			HomePath            = homePath;

			var binPath         = Path.Combine (HomePath, "bin");
			JarPath             = ProcessUtils.FindExecutablesInDirectory (binPath, "jar").FirstOrDefault ();
			JavaPath            = ProcessUtils.FindExecutablesInDirectory (binPath, "java").FirstOrDefault ();
			JavacPath           = ProcessUtils.FindExecutablesInDirectory (binPath, "javac").FirstOrDefault ();
			JdkJvmPath = OS.IsMac
				? FindLibrariesInDirectory (Path.Combine (HomePath, "jre"), "jli").FirstOrDefault ()
				: FindLibrariesInDirectory (Path.Combine (HomePath, "jre"), "jvm").FirstOrDefault ();

			ValidateFile ("jar",    JarPath);
			ValidateFile ("java",   JavaPath);
			ValidateFile ("javac",  JavacPath);
			ValidateFile ("jvm",    JdkJvmPath);

			var includes        = new List<string> ();
			var jdkInclude      = Path.Combine (HomePath, "include");

			if (Directory.Exists (jdkInclude)) {
				includes.Add (jdkInclude);
				includes.AddRange (Directory.GetDirectories (jdkInclude));
			}


			ReleaseProperties   = GetReleaseProperties();

			IncludePath         = new ReadOnlyCollection<string> (includes);

			javaProperties      = new Lazy<Dictionary<string, List<string>>> (GetJavaProperties, LazyThreadSafetyMode.ExecutionAndPublication);
			javaVersion         = new Lazy<Version> (GetJavaVersion, LazyThreadSafetyMode.ExecutionAndPublication);
		}

		public JdkInfo (string homePath, string locator)
			: this (homePath)
		{
			Locator             = locator;
		}

		public override string ToString()
		{
			return $"JdkInfo(Version={Version}, Vendor=\"{Vendor}\", HomePath=\"{HomePath}\", Locator=\"{Locator}\")";
		}

		public bool GetJavaSettingsPropertyValues (string key, out IEnumerable<string> value)
		{
			value       = null;
			var props   = javaProperties.Value;
			if (props.TryGetValue (key, out var v)) {
				value = v;
				return true;
			}
			return false;
		}

		public bool GetJavaSettingsPropertyValue (string key, out string value)
		{
			value       = null;
			var props   = javaProperties.Value;
			if (props.TryGetValue (key, out var v)) {
				if (v.Count > 1)
					throw new InvalidOperationException ($"Requested to get one string value when property `{key}` contains `{v.Count}` values.");
				value   = v [0];
				return true;
			}
			return false;
		}

		static IEnumerable<string> FindLibrariesInDirectory (string dir, string libraryName)
		{
			if (!Directory.Exists (dir))
				return Enumerable.Empty<string> ();
			var library = string.Format (OS.NativeLibraryFormat, libraryName);
			return Directory.EnumerateFiles (dir, library, SearchOption.AllDirectories);
		}

		void ValidateFile (string name, string path)
		{
			if (path == null || !File.Exists (path))
				throw new ArgumentException ($"Could not find required file `{name}` within `{HomePath}`; is this a valid JDK?", "homePath");
		}

		static  Regex   NonDigitMatcher     = new Regex (@"[^\d]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		Version GetJavaVersion ()
		{
			string version = null;
			if (ReleaseProperties.TryGetValue ("JAVA_VERSION", out version) && !string.IsNullOrEmpty (version)) {
				version = GetParsableVersion (version);
				if (ReleaseProperties.TryGetValue ("BUILD_NUMBER", out var build) && !string.IsNullOrEmpty (build))
					version += "." + build;
			}
			else if (GetJavaSettingsPropertyValue ("java.version", out version) && !string.IsNullOrEmpty (version)) {
				version = GetParsableVersion (version);
			}
			if (string.IsNullOrEmpty (version))
				throw new NotSupportedException ("Could not determine Java version.");
			var normalizedVersion   = NonDigitMatcher.Replace (version, ".");
			var versionParts        = normalizedVersion.Split (new[]{"."}, StringSplitOptions.RemoveEmptyEntries);

			try {
				if (versionParts.Length < 2)
					return null;
				if (versionParts.Length == 2)
					return new Version (major: int.Parse (versionParts [0]), minor: int.Parse (versionParts [1]));
				if (versionParts.Length == 3)
					return new Version (major: int.Parse (versionParts [0]), minor: int.Parse (versionParts [1]), build: int.Parse (versionParts [2]));
				// We just ignore elements 4+
				return new Version (major: int.Parse (versionParts [0]), minor: int.Parse (versionParts [1]), build: int.Parse (versionParts [2]), revision: int.Parse (versionParts [3]));
			}
			catch (Exception) {
				return null;
			}
		}

		static string GetParsableVersion (string version)
		{
			if (!version.Contains ("."))
				version += ".0";
			return version;
		}

		ReadOnlyDictionary<string, string> GetReleaseProperties ()
		{
			var props       = new Dictionary<string, string> ();
			var releasePath = Path.Combine (HomePath, "release");
			if (!File.Exists (releasePath))
				return new ReadOnlyDictionary<string, string>(props);

			using (var release = File.OpenText (releasePath)) {
				string line;
				while ((line = release.ReadLine ()) != null) {
					line            = line.Trim ();
					const string PropertyDelim  = "=";
					int delim = line.IndexOf (PropertyDelim, StringComparison.Ordinal);
					if (delim < 0) {
						props [line] = "";
						continue;
					}
					string  key     = line.Substring (0, delim).Trim ();
					string  value   = line.Substring (delim + PropertyDelim.Length).Trim ();
					if (value.StartsWith ("\"", StringComparison.Ordinal) && value.EndsWith ("\"", StringComparison.Ordinal)) {
						value       = value.Substring (1, value.Length - 2);
					}
					props [key] = value;
				}
			}
			return new ReadOnlyDictionary<string, string>(props);
		}

		Dictionary<string, List<string>> GetJavaProperties ()
		{
			return GetJavaProperties (ProcessUtils.FindExecutablesInDirectory (Path.Combine (HomePath, "bin"), "java").First ());
		}

		static bool AnySystemJavasInstalled ()
		{
			if (OS.IsMac) {
				string path = Path.Combine (Path.DirectorySeparatorChar + "System", "Library", "Java", "JavaVirtualMachines");
				if (!Directory.Exists (path)) {
					return false;
				}

				string[] dirs = Directory.GetDirectories (path);
				if (dirs == null || dirs.Length == 0) {
					return false;
				}
			}

			return true;
		}

		static Dictionary<string, List<string>> GetJavaProperties (string java)
		{
			var javaProps   = new ProcessStartInfo {
				FileName    = java,
				Arguments   = "-XshowSettings:properties -version",
			};

			var     props   = new Dictionary<string, List<string>> ();
			string  curKey  = null;

			if (!AnySystemJavasInstalled () && (java == "/usr/bin/java" || java == "java"))
				return props;

			ProcessUtils.Exec (javaProps, (o, e) => {
					const string ContinuedValuePrefix   = "        ";
					const string NewValuePrefix         = "    ";
					const string NameValueDelim         = " = ";
					if (string.IsNullOrEmpty (e.Data))
						return;
					if (e.Data.StartsWith (ContinuedValuePrefix, StringComparison.Ordinal)) {
						if (curKey == null)
							throw new InvalidOperationException ($"Unknown property key for value {e.Data}!");
						props [curKey].Add (e.Data.Substring (ContinuedValuePrefix.Length));
						return;
					}
					if (e.Data.StartsWith (NewValuePrefix, StringComparison.Ordinal)) {
						var delim = e.Data.IndexOf (NameValueDelim, StringComparison.Ordinal);
						if (delim <= 0)
							return;
						curKey      = e.Data.Substring (NewValuePrefix.Length, delim - NewValuePrefix.Length);
						var value   = e.Data.Substring (delim + NameValueDelim.Length);
						List<string> values;
						if (!props.TryGetValue (curKey, out values))
							props.Add (curKey, values = new List<string> ());
						values.Add (value);
					}
			});

			return props;
		}

		public static IEnumerable<JdkInfo> GetKnownSystemJdkInfos (Action<TraceLevel, string> logger = null)
		{
			logger  = logger ?? AndroidSdkInfo.DefaultConsoleLogger;

			return GetWindowsJdks (logger)
				.Concat (GetConfiguredJdks (logger))
				.Concat (GetMacOSMicrosoftJdks (logger))
				.Concat (GetJavaHomeEnvironmentJdks (logger))
				.Concat (GetPathEnvironmentJdks (logger))
				.Concat (GetLibexecJdks (logger))
				.Concat (GetJavaAlternativesJdks (logger))
				;
		}

		static IEnumerable<JdkInfo> GetConfiguredJdks (Action<TraceLevel, string> logger)
		{
			return GetConfiguredJdkPaths (logger)
				.Select (p => TryGetJdkInfo (p, logger, "monodroid-config.xml"))
				.Where (jdk => jdk != null)
				.OrderByDescending (jdk => jdk, JdkInfoVersionComparer.Default);
		}

		static IEnumerable<string> GetConfiguredJdkPaths (Action<TraceLevel, string> logger)
		{
			var config = AndroidSdkUnix.GetUnixConfigFile (logger);
			foreach (var java_sdk in config.Root.Elements ("java-sdk")) {
				var path    = (string) java_sdk.Attribute ("path");
				yield return path;
			}
		}

		internal static IEnumerable<JdkInfo> GetMacOSMicrosoftJdks (Action<TraceLevel, string> logger)
		{
			return GetMacOSMicrosoftJdkPaths ()
				.Select (p => TryGetJdkInfo (p, logger, "$HOME/Library/Developer/Xamarin/jdk"))
				.Where (jdk => jdk != null)
				.OrderByDescending (jdk => jdk, JdkInfoVersionComparer.Default);
		}

		static IEnumerable<string> GetMacOSMicrosoftJdkPaths ()
		{
			var jdks    = AppDomain.CurrentDomain.GetData ($"GetMacOSMicrosoftJdkPaths jdks override! {typeof (JdkInfo).AssemblyQualifiedName}")
				?.ToString ();
			if (jdks == null) {
				var home    = Environment.GetFolderPath (Environment.SpecialFolder.Personal);
				jdks        = Path.Combine (home, "Library", "Developer", "Xamarin", "jdk");
			}
			if (!Directory.Exists (jdks))
				return Enumerable.Empty <string> ();

			return Directory.EnumerateDirectories (jdks);
		}

		static JdkInfo TryGetJdkInfo (string path, Action<TraceLevel, string> logger, string locator)
		{
			JdkInfo jdk = null;
			try {
				jdk = new JdkInfo (path, locator);
			}
			catch (Exception e) {
				logger (TraceLevel.Warning, $"Not a valid JDK directory: `{path}`; via locator: {locator}");
				logger (TraceLevel.Verbose, e.ToString ());
			}
			return jdk;
		}

		static IEnumerable<JdkInfo> GetWindowsJdks (Action<TraceLevel, string> logger)
		{
			if (!OS.IsWindows)
				return Enumerable.Empty<JdkInfo> ();
			return AndroidSdkWindows.GetJdkInfos (logger);
		}

		static IEnumerable<JdkInfo> GetJavaHomeEnvironmentJdks (Action<TraceLevel, string> logger)
		{
			var java_home = Environment.GetEnvironmentVariable ("JAVA_HOME");
			if (string.IsNullOrEmpty (java_home))
				yield break;
			var jdk = TryGetJdkInfo (java_home, logger, "$JAVA_HOME");
			if (jdk != null)
				yield return jdk;
		}

		// macOS
		static IEnumerable<JdkInfo> GetLibexecJdks (Action<TraceLevel, string> logger)
		{
			return GetLibexecJdkPaths (logger)
				.Distinct ()
				.Select (p => TryGetJdkInfo (p, logger, "`/usr/libexec/java_home -X`"))
				.Where (jdk => jdk != null)
				.OrderByDescending (jdk => jdk, JdkInfoVersionComparer.Default);
		}

		static IEnumerable<string> GetLibexecJdkPaths (Action<TraceLevel, string> logger)
		{
			var java_home	= Path.GetFullPath ("/usr/libexec/java_home");
			if (!File.Exists (java_home)) {
				yield break;
			}
			var jhp = new ProcessStartInfo {
				FileName    = java_home,
				Arguments   = "-X",
			};
			var xml = new StringBuilder ();
			ProcessUtils.Exec (jhp, (o, e) => {
					if (string.IsNullOrEmpty (e.Data))
						return;
					xml.Append (e.Data);
			}, includeStderr: false);
			var plist   = XElement.Parse (xml.ToString ());
			foreach (var info in plist.Elements ("array").Elements ("dict")) {
				var JVMHomePath = (XNode) info.Elements ("key").FirstOrDefault (e => e.Value == "JVMHomePath");
				if (JVMHomePath == null)
					continue;
				while (JVMHomePath.NextNode.NodeType != XmlNodeType.Element)
					JVMHomePath = JVMHomePath.NextNode;
				var strElement  = (XElement) JVMHomePath.NextNode;
				var path        = strElement.Value;
				yield return path;
			}
		}

		// Linux; Ubuntu & Derivatives
		static IEnumerable<JdkInfo> GetJavaAlternativesJdks (Action<TraceLevel, string> logger)
		{
			return GetJavaAlternativesJdkPaths ()
				.Distinct ()
				.Select (p => TryGetJdkInfo (p, logger, "`/usr/sbin/update-java-alternatives -l`"))
				.Where (jdk => jdk != null);
		}

		static IEnumerable<string> GetJavaAlternativesJdkPaths ()
		{
			var alternatives  = Path.GetFullPath ("/usr/sbin/update-java-alternatives");
			if (!File.Exists (alternatives))
				return Enumerable.Empty<string> ();

			var psi     = new ProcessStartInfo {
				FileName    = alternatives,
				Arguments   = "-l",
			};
			var paths   = new List<string> ();
			ProcessUtils.Exec (psi, (o, e) => {
					if (string.IsNullOrWhiteSpace (e.Data))
						return;
					// Example line:
					//  java-1.8.0-openjdk-amd64       1081       /usr/lib/jvm/java-1.8.0-openjdk-amd64
					var columns = e.Data.Split (new[]{ ' ' }, StringSplitOptions.RemoveEmptyEntries);
					if (columns.Length <= 2)
						return;
					paths.Add (columns [2]);
			});
			return paths;
		}

		// Linux; Fedora
		static IEnumerable<JdkInfo> GetLibJvmJdks (Action<TraceLevel, string> logger)
		{
			return GetLibJvmJdkPaths ()
				.Distinct ()
				.Select (p => TryGetJdkInfo (p, logger, "`ls /usr/lib/jvm/*`"))
				.Where (jdk => jdk != null)
				.OrderByDescending (jdk => jdk, JdkInfoVersionComparer.Default);
		}

		static IEnumerable<string> GetLibJvmJdkPaths ()
		{
			var jvm = "/usr/lib/jvm";
			if (!Directory.Exists (jvm))
				yield break;

			foreach (var jdk in Directory.EnumerateDirectories (jvm)) {
				var release = Path.Combine (jdk, "release");
				if (File.Exists (release))
					yield return jdk;
			}
		}

		// Last-ditch fallback!
		static IEnumerable<JdkInfo> GetPathEnvironmentJdks (Action<TraceLevel, string> logger)
		{
			return GetPathEnvironmentJdkPaths ()
				.Select (p => TryGetJdkInfo (p, logger, "$PATH"))
				.Where (jdk => jdk != null);
		}

		static IEnumerable<string> GetPathEnvironmentJdkPaths ()
		{
			foreach (var java in ProcessUtils.FindExecutablesInPath ("java")) {
				var props   = GetJavaProperties (java);
				if (props.TryGetValue ("java.home", out var java_homes)) {
					var java_home   = java_homes [0];
					// `java -XshowSettings:properties -version 2>&1 | grep java.home` ends with `/jre` on macOS.
					// We need the parent dir so we can properly lookup the `include` directories
					if (java_home.EndsWith ("jre", StringComparison.OrdinalIgnoreCase)) {
						java_home = Path.GetDirectoryName (java_home);
					}
					yield return java_home;
				}
			}
		}
	}

	class JdkInfoVersionComparer : IComparer<JdkInfo>
	{
		public  static  readonly    IComparer<JdkInfo> Default = new JdkInfoVersionComparer ();

		public int Compare (JdkInfo x, JdkInfo y)
		{
			if (x.Version != null && y.Version != null)
				return x.Version.CompareTo (y.Version);
			return 0;
		}
	}
}

namespace Xamarin.Android.Tools
{
	public static class ProcessUtils
	{
		static string[] ExecutableFileExtensions;

		static ProcessUtils ()
		{
			var pathExt     = Environment.GetEnvironmentVariable ("PATHEXT");
			var pathExts    = pathExt?.Split (new char [] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries) ?? new string [0];

			ExecutableFileExtensions    = pathExts;
		}

		public static async System.Threading.Tasks.Task<int> StartProcess (ProcessStartInfo psi, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken, Action<Process> onStarted = null)
		{
			cancellationToken.ThrowIfCancellationRequested ();
			psi.UseShellExecute = false;
			psi.RedirectStandardOutput |= stdout != null;
			psi.RedirectStandardError |= stderr != null;

			var process = new Process {
				StartInfo = psi,
				EnableRaisingEvents = true,
			};

			Task output = Task.FromResult (true);
			Task error = Task.FromResult (true);
			Task exit = WaitForExitAsync (process);
			using (process) {
				process.Start ();
				if (onStarted != null)
					onStarted (process);

				// If the token is cancelled while we're running, kill the process.
				// Otherwise once we finish the Task.WhenAll we can remove this registration
				// as there is no longer any need to Kill the process.
				//
				// We wrap `stdout` and `stderr` in syncronized wrappers for safety in case they
				// end up writing to the same buffer, or they are the same object.
				using (cancellationToken.Register (() => KillProcess (process))) {
					if (psi.RedirectStandardOutput)
						output = ReadStreamAsync (process.StandardOutput, TextWriter.Synchronized (stdout));

					if (psi.RedirectStandardError)
						error = ReadStreamAsync (process.StandardError, TextWriter.Synchronized (stderr));

					await Task.WhenAll (new [] { output, error, exit }).ConfigureAwait (false);
				}
				// If we invoke 'KillProcess' our output, error and exit tasks will all complete normally.
				// To protected against passing the user incomplete data we have to call
				// `cancellationToken.ThrowIfCancellationRequested ()` here.
				cancellationToken.ThrowIfCancellationRequested ();
				return process.ExitCode;
			}
		}

		static void KillProcess (Process p)
		{
			try {
				p.Kill ();
			} catch (InvalidOperationException) {
				// If the process has already exited this could happen
			}
		}

		static Task WaitForExitAsync (Process process)
		{
			var exitDone = new TaskCompletionSource<bool> ();
			process.Exited += (o, e) => exitDone.TrySetResult (true);
			return exitDone.Task;
		}

		static async System.Threading.Tasks.Task ReadStreamAsync (StreamReader stream, TextWriter destination)
		{
			int read;
			var buffer = new char [4096];
			while ((read = await stream.ReadAsync (buffer, 0, buffer.Length).ConfigureAwait (false)) > 0)
				destination.Write (buffer, 0, read);
		}

		/// <summary>
		/// Executes an Android Sdk tool and returns a result. The result is based on a function of the command output.
		/// </summary>
		public static System.Threading.Tasks.Task<TResult> ExecuteToolAsync<TResult> (string exe, Func<string, TResult> result, CancellationToken token, Action<Process> onStarted = null)
		{
			var tcs = new TaskCompletionSource<TResult> ();

			var log = new StringWriter ();
			var error = new StringWriter ();

			var psi = new ProcessStartInfo (exe);
			psi.CreateNoWindow = true;
			psi.RedirectStandardInput = onStarted != null;

			var processTask = ProcessUtils.StartProcess (psi, log, error, token, onStarted);
			var exeName = Path.GetFileName (exe);

			processTask.ContinueWith (t => {
				var output = log.ToString ();
				var errorOutput = error.ToString ();
				log.Dispose ();
				error.Dispose ();

				if (t.IsCanceled) {
					tcs.TrySetCanceled ();
					return;
				}

				if (t.IsFaulted) {
					tcs.TrySetException (t.Exception.Flatten ().InnerException);
					return;
				}

				if (t.Result == 0) {
					tcs.TrySetResult (result != null ? result (output) : default (TResult));
				} else {
					var errorMessage = !string.IsNullOrEmpty (errorOutput) ? errorOutput : output;

					tcs.TrySetException (new InvalidOperationException (string.IsNullOrEmpty (errorMessage) ? exeName + " returned non-zero exit code" : string.Format ("{0} : {1}", t.Result, errorMessage)));
				}
			}, TaskContinuationOptions.ExecuteSynchronously);

			return tcs.Task;
		}

		internal static void Exec (ProcessStartInfo processStartInfo, DataReceivedEventHandler output, bool includeStderr = true)
		{
			processStartInfo.UseShellExecute         = false;
			processStartInfo.RedirectStandardInput   = false;
			processStartInfo.RedirectStandardOutput  = true;
			processStartInfo.RedirectStandardError   = true;
			processStartInfo.CreateNoWindow          = true;
			processStartInfo.WindowStyle             = ProcessWindowStyle.Hidden;

			var p = new Process () {
				StartInfo   = processStartInfo,
			};
			p.OutputDataReceived    += output;
			if (includeStderr) {
				p.ErrorDataReceived   += output;
			}

			using (p) {
				p.Start ();
				p.BeginOutputReadLine ();
				p.BeginErrorReadLine ();
				p.WaitForExit ();
			}
		}

		internal static IEnumerable<string> FindExecutablesInPath (string executable)
		{
			var path        = Environment.GetEnvironmentVariable ("PATH");
			var pathDirs    = path.Split (new char[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);

			foreach (var dir in pathDirs) {
				foreach (var exe in FindExecutablesInDirectory (dir, executable)) {
					yield return exe;
				}
			}
		}

		internal static IEnumerable<string> FindExecutablesInDirectory (string dir, string executable)
		{
			foreach (var exe in ExecutableFiles (executable)) {
				var exePath = Path.Combine (dir, exe);
				if (File.Exists (exePath))
					yield return exePath;
			}
		}

		internal static IEnumerable<string> ExecutableFiles (string executable)
		{
			if (ExecutableFileExtensions == null || ExecutableFileExtensions.Length == 0) {
				yield return executable;
				yield break;
			}

			foreach (var ext in ExecutableFileExtensions)
				yield return Path.ChangeExtension (executable, ext);
			yield return executable;
		}
	}
}

namespace Xamarin.Android.Tools
{
	public class OS
	{
		public readonly static bool IsWindows;
		public readonly static bool IsMac;

		internal readonly static string ProgramFilesX86;

		internal readonly static string NativeLibraryFormat;

		static OS ()
		{
			IsWindows = Path.DirectorySeparatorChar == '\\';
			IsMac = !IsWindows && IsRunningOnMac ();

			if (IsWindows) {
				ProgramFilesX86 = GetProgramFilesX86 ();
			}

			if (IsWindows)
				NativeLibraryFormat = "{0}.dll";
			if (IsMac)
				NativeLibraryFormat = "lib{0}.dylib";
			if (!IsWindows && !IsMac)
				NativeLibraryFormat = "lib{0}.so";
		}

		//From Managed.Windows.Forms/XplatUI
		static bool IsRunningOnMac ()
		{
			IntPtr buf = IntPtr.Zero;
			try {
				buf = Marshal.AllocHGlobal (8192);
				// This is a hacktastic way of getting sysname from uname ()
				if (uname (buf) == 0) {
					string os = System.Runtime.InteropServices.Marshal.PtrToStringAnsi (buf);
					if (os == "Darwin")
						return true;
				}
			} catch {
			} finally {
				if (buf != IntPtr.Zero)
					System.Runtime.InteropServices.Marshal.FreeHGlobal (buf);
			}
			return false;
		}

		[DllImport ("libc")]
		static extern int uname (IntPtr buf);

		static string GetProgramFilesX86 ()
		{
			//SpecialFolder.ProgramFilesX86 is broken on 32-bit WinXP
			if (IntPtr.Size == 8) {
				return Environment.GetFolderPath (Environment.SpecialFolder.ProgramFilesX86);
			} else {
				return Environment.GetFolderPath (Environment.SpecialFolder.ProgramFiles);
			}
		}

		internal static string GetXamarinAndroidCacheDir ()
		{
			if (IsMac) {
				var home = Environment.GetFolderPath (Environment.SpecialFolder.Personal);
				return Path.Combine (home, "Library", "Caches", "Xamarin.Android");
			} else if (IsWindows) {
				var localAppData = Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData);
				return Path.Combine (localAppData, "Xamarin.Android", "Cache");
			} else {
				var home = Environment.GetFolderPath (Environment.SpecialFolder.Personal);
				var xdgCacheHome = Environment.GetEnvironmentVariable ("XDG_CACHE_HOME");
				if (string.IsNullOrEmpty (xdgCacheHome)) {
					xdgCacheHome = Path.Combine (home, ".cache");
				}
				return Path.Combine (xdgCacheHome, "Xamarin.Android");
			}
		}
	}

	public static class KernelEx {
		[DllImport ("kernel32.dll", CharSet = CharSet.Auto)]
		static extern int GetLongPathName (
			[MarshalAs (UnmanagedType.LPTStr)] string path,
			[MarshalAs (UnmanagedType.LPTStr)] StringBuilder longPath,
			int longPathLength
		);

		public static string GetLongPathName (string path)
		{
			StringBuilder sb = new StringBuilder (255);
			GetLongPathName (path, sb, sb.Capacity);
			return sb.ToString ();
		}

		[DllImport ("kernel32.dll", CharSet = CharSet.Auto)]
		static extern int GetShortPathName (
			[MarshalAs (UnmanagedType.LPTStr)] string path,
			[MarshalAs (UnmanagedType.LPTStr)] StringBuilder shortPath,
			int shortPathLength
		);

		public static string GetShortPathName (string path)
		{
			StringBuilder sb = new StringBuilder (255);
			GetShortPathName (path, sb, sb.Capacity);
			return sb.ToString ();
		}
	}

	internal static class RegistryEx
	{
		const string ADVAPI = "advapi32.dll";

		public static UIntPtr CurrentUser = (UIntPtr)0x80000001;
		public static UIntPtr LocalMachine = (UIntPtr)0x80000002;

		[DllImport (ADVAPI, CharSet = CharSet.Unicode, SetLastError = true)]
		static extern int RegOpenKeyEx (UIntPtr hKey, string subKey, uint reserved, uint sam, out UIntPtr phkResult);

		[DllImport (ADVAPI, CharSet = CharSet.Unicode, SetLastError = true)]
		static extern int RegQueryValueExW (UIntPtr hKey, string lpValueName, int lpReserved, out uint lpType,
			StringBuilder lpData, ref uint lpcbData);

		[DllImport (ADVAPI, CharSet = CharSet.Unicode, SetLastError = true)]
		static extern int RegSetValueExW (UIntPtr hKey, string lpValueName, int lpReserved,
			uint dwType, string data, uint cbData);

		[DllImport (ADVAPI, CharSet = CharSet.Unicode, SetLastError = true)]
		static extern int RegSetValueExW (UIntPtr hKey, string lpValueName, int lpReserved,
			uint dwType, IntPtr data, uint cbData);

		[DllImport (ADVAPI, CharSet = CharSet.Unicode, SetLastError = true)]
		static extern int RegCreateKeyEx (UIntPtr hKey, string subKey, uint reserved, string @class, uint options,
			uint samDesired, IntPtr lpSecurityAttributes, out UIntPtr phkResult, out Disposition lpdwDisposition);

		[DllImport ("advapi32.dll", SetLastError = true)]
		static extern int RegCloseKey (UIntPtr hKey);

		public static string GetValueString (UIntPtr key, string subkey, string valueName, Wow64 wow64)
		{
			UIntPtr regKeyHandle;
			uint sam = (uint)Rights.QueryValue + (uint)wow64;
			if (RegOpenKeyEx (key, subkey, 0, sam, out regKeyHandle) != 0)
				return null;

			try {
				uint type;
				var sb = new StringBuilder (2048);
				uint cbData = (uint) sb.Capacity;
				if (RegQueryValueExW (regKeyHandle, valueName, 0, out type, sb, ref cbData) == 0) {
					return sb.ToString ();
				}
				return null;
			} finally {
				RegCloseKey (regKeyHandle);
			}
		}

		public static void SetValueString (UIntPtr key, string subkey, string valueName, string value, Wow64 wow64)
		{
			UIntPtr regKeyHandle;
			uint sam = (uint)(Rights.CreateSubKey | Rights.SetValue) + (uint)wow64;
			uint options = (uint) Options.NonVolatile;
			Disposition disposition;
			if (RegCreateKeyEx (key, subkey, 0, null, options, sam, IntPtr.Zero, out regKeyHandle, out disposition) != 0) {
				throw new Exception ("Could not open or craete key");
			}

			try {
				uint type = (uint)ValueType.String;
				uint lenBytesPlusNull = ((uint)value.Length + 1) * 2;
				var result = RegSetValueExW (regKeyHandle, valueName, 0, type, value, lenBytesPlusNull);
				if (result != 0)
					throw new Exception (string.Format ("Error {0} setting registry key '{1}{2}@{3}'='{4}'",
						result, key, subkey, valueName, value));
			} finally {
				RegCloseKey (regKeyHandle);
			}
		}

		[Flags]
		enum Rights : uint
		{
			None = 0,
			QueryValue = 0x0001,
			SetValue = 0x0002,
			CreateSubKey = 0x0004,
			EnumerateSubKey = 0x0008,
		}

		enum Options
		{
			BackupRestore = 0x00000004,
			CreateLink = 0x00000002,
			NonVolatile = 0x00000000,
			Volatile = 0x00000001,
		}

		public enum Wow64 : uint
		{
			Key64 = 0x0100,
			Key32 = 0x0200,
		}

		enum ValueType : uint
		{
			None = 0, //REG_NONE
			String = 1, //REG_SZ
			UnexpandedString = 2, //REG_EXPAND_SZ
			Binary = 3, //REG_BINARY
			DWord = 4, //REG_DWORD
			DWordLittleEndian = 4, //REG_DWORD_LITTLE_ENDIAN
			DWordBigEndian = 5, //REG_DWORD_BIG_ENDIAN
			Link = 6, //REG_LINK
			MultiString = 7, //REG_MULTI_SZ
			ResourceList = 8, //REG_RESOURCE_LIST
			FullResourceDescriptor = 9, //REG_FULL_RESOURCE_DESCRIPTOR
			ResourceRequirementsList = 10, //REG_RESOURCE_REQUIREMENTS_LIST
			QWord = 11, //REG_QWORD
			QWordLittleEndian = 11, //REG_QWORD_LITTLE_ENDIAN
		}

		enum Disposition : uint
		{
			CreatedNewKey  = 0x00000001,
			OpenedExistingKey = 0x00000002,
		}
	}
}

namespace Xamarin.Android.Tools
{
	abstract class AndroidSdkBase
	{
		string[] allAndroidSdks = null;
		string[] allAndroidNdks = null;

		public string[] AllAndroidSdks {
			get {
				if (allAndroidSdks == null)
					allAndroidSdks = GetAllAvailableAndroidSdks ().Distinct ().ToArray ();
				return allAndroidSdks;
			}
		}
		public string[] AllAndroidNdks {
			get {
				if (allAndroidNdks == null)
					allAndroidNdks = GetAllAvailableAndroidNdks ().Distinct ().ToArray ();
				return allAndroidNdks;
			}
		}

		public readonly Action<TraceLevel, string> Logger;

		public AndroidSdkBase (Action<TraceLevel, string> logger)
		{
			Logger  = logger;
		}

		public string AndroidSdkPath { get; private set; }
		public string AndroidNdkPath { get; private set; }
		public string JavaSdkPath { get; private set; }
		public string JavaBinPath { get; private set; }
		public string AndroidToolsPath { get; private set; }
		public string AndroidPlatformToolsPath { get; private set; }
		public string AndroidToolsPathShort { get; private set; }
		public string AndroidPlatformToolsPathShort { get; private set; }

		public virtual string Adb { get; protected set; } = "adb";
		public virtual string Android { get; protected set; } = "android";
		public virtual string Emulator { get; protected set; } = "emulator";
		public virtual string Monitor { get; protected set; } = "monitor";
		public virtual string ZipAlign { get; protected set; } = "zipalign";
		public virtual string JarSigner { get; protected set; } = "jarsigner";
		public virtual string KeyTool { get; protected set; } = "keytool";

		public virtual string NdkStack { get; protected set; } = "ndk-stack";
		public abstract string NdkHostPlatform32Bit { get; }
		public abstract string NdkHostPlatform64Bit { get; }
		public virtual string Javac { get; protected set; } = "javac";

		public abstract string PreferedAndroidSdkPath { get; }
		public abstract string PreferedAndroidNdkPath { get; }
		public abstract string PreferedJavaSdkPath { get; }

		public virtual void Initialize (string androidSdkPath = null, string androidNdkPath = null, string javaSdkPath = null)
		{
			androidSdkPath  = androidSdkPath ?? PreferedAndroidSdkPath;
			androidNdkPath  = androidNdkPath ?? PreferedAndroidNdkPath;
			javaSdkPath     = javaSdkPath ?? PreferedJavaSdkPath;

			AndroidSdkPath  = ValidateAndroidSdkLocation (androidSdkPath) ? androidSdkPath : AllAndroidSdks.FirstOrDefault ();
			AndroidNdkPath  = ValidateAndroidNdkLocation (androidNdkPath) ? androidNdkPath : AllAndroidNdks.FirstOrDefault ();
			JavaSdkPath     = ValidateJavaSdkLocation (javaSdkPath) ? javaSdkPath : GetJavaSdkPath ();

			if (!string.IsNullOrEmpty (JavaSdkPath)) {
				JavaBinPath = Path.Combine (JavaSdkPath, "bin");
			} else {
				JavaBinPath = null;
			}

			if (!string.IsNullOrEmpty (AndroidSdkPath)) {
				AndroidToolsPath = Path.Combine (AndroidSdkPath, "tools");
				AndroidToolsPathShort = GetShortFormPath (AndroidToolsPath);
				AndroidPlatformToolsPath = Path.Combine (AndroidSdkPath, "platform-tools");
				AndroidPlatformToolsPathShort = GetShortFormPath (AndroidPlatformToolsPath);
			} else {
				AndroidToolsPath = null;
				AndroidToolsPathShort = null;
				AndroidPlatformToolsPath = null;
				AndroidPlatformToolsPathShort = null;
			}

			if (!string.IsNullOrEmpty (AndroidNdkPath)) {
				// It would be nice if .NET had real globbing support in System.IO...
				string toolchainsDir = Path.Combine (AndroidNdkPath, "toolchains");
				if (Directory.Exists (toolchainsDir)) {
					IsNdk64Bit = Directory.EnumerateDirectories (toolchainsDir, "arm-linux-androideabi-*")
						.Any (dir => Directory.Exists (Path.Combine (dir, "prebuilt", NdkHostPlatform64Bit)));
				}
			}
			// we need to look for extensions other than the default .exe|.bat
			// google have a habbit of changing them.
			Adb = GetExecutablePath (AndroidPlatformToolsPath, Adb);
			Android = GetExecutablePath (AndroidToolsPath, Android);
			Emulator = GetExecutablePath (AndroidToolsPath, Emulator);
			Monitor = GetExecutablePath (AndroidToolsPath, Monitor);
			NdkStack = GetExecutablePath (AndroidNdkPath, NdkStack);
		}

		protected abstract IEnumerable<string> GetAllAvailableAndroidSdks ();
		protected abstract IEnumerable<string> GetAllAvailableAndroidNdks ();
		protected abstract string GetJavaSdkPath ();
		protected abstract string GetShortFormPath (string path);

		public abstract void SetPreferredAndroidSdkPath (string path);
		public abstract void SetPreferredJavaSdkPath (string path);
		public abstract void SetPreferredAndroidNdkPath (string path);

		public bool IsNdk64Bit { get; private set; }

		public string NdkHostPlatform {
			get { return IsNdk64Bit ? NdkHostPlatform64Bit : NdkHostPlatform32Bit; }
		}

		/// <summary>
		/// Checks that a value is the location of an Android SDK.
		/// </summary>
		public bool ValidateAndroidSdkLocation (string loc)
		{
			bool result = !string.IsNullOrEmpty (loc) && ProcessUtils.FindExecutablesInDirectory (Path.Combine (loc, "platform-tools"), Adb).Any ();
			Logger (TraceLevel.Verbose, $"{nameof (ValidateAndroidSdkLocation)}: `{loc}`, result={result}");
			return result;
		}

		/// <summary>
		/// Checks that a value is the location of a Java SDK.
		/// </summary>
		public virtual bool ValidateJavaSdkLocation (string loc)
		{
			bool result = !string.IsNullOrEmpty (loc) && ProcessUtils.FindExecutablesInDirectory (Path.Combine (loc, "bin"), JarSigner).Any ();
			Logger (TraceLevel.Verbose, $"{nameof (ValidateJavaSdkLocation)}: `{loc}`, result={result}");
			return result;
		}

		/// <summary>
		/// Checks that a value is the location of an Android SDK.
		/// </summary>
		public bool ValidateAndroidNdkLocation (string loc)
		{
			bool result = !string.IsNullOrEmpty (loc) && ProcessUtils.FindExecutablesInDirectory (loc, NdkStack).Any ();
			Logger (TraceLevel.Verbose, $"{nameof (ValidateAndroidNdkLocation)}: `{loc}`, result={result}");
			return result;
		}

		protected static string NullIfEmpty (string s)
		{
			if (s == null || s.Length != 0)
				return s;

			return null;
		}

		static string GetExecutablePath (string dir, string exe)
		{
			if (string.IsNullOrEmpty (dir))
				return exe;

			foreach (var e in ProcessUtils.ExecutableFiles (exe))
				if (File.Exists (Path.Combine (dir, e)))
					return e;
			return exe;
		}
	}
}

namespace Xamarin.Android.Tools
{
	class AndroidSdkUnix : AndroidSdkBase
	{
		// See comments above UnixConfigPath for explanation on why these are needed
		static readonly string sudo_user;
		static readonly string user;
		static readonly bool need_chown;

		static AndroidSdkUnix ()
		{
			sudo_user = Environment.GetEnvironmentVariable ("SUDO_USER");
			if (String.IsNullOrEmpty (sudo_user))
				return;

			user = Environment.GetEnvironmentVariable ("USER");
			if (String.IsNullOrEmpty (user) || String.Compare (user, sudo_user, StringComparison.Ordinal) == 0)
				return;
			need_chown = true;
		}

		public AndroidSdkUnix (Action<TraceLevel, string> logger)
			: base (logger)
		{
		}

		public override string NdkHostPlatform32Bit {
			get { return OS.IsMac ? "darwin-x86" : "linux-x86"; }
		}
		public override string NdkHostPlatform64Bit {
			get { return OS.IsMac ? "darwin-x86_64" : "linux-x86_64"; }
		}

		public override string PreferedAndroidSdkPath { 
			get {
				var config_file = GetUnixConfigFile (Logger);
				var androidEl = config_file.Root.Element ("android-sdk");

				if (androidEl != null) {
					var path = (string)androidEl.Attribute ("path");

					if (ValidateAndroidSdkLocation (path))
						return path;
				}
				return null;
			}
		}

		public override string PreferedAndroidNdkPath { 
			get {
				var config_file = GetUnixConfigFile (Logger);
				var androidEl = config_file.Root.Element ("android-ndk");

				if (androidEl != null) {
					var path = (string)androidEl.Attribute ("path");

					if (ValidateAndroidNdkLocation (path))
						return path;
				}
				return null;
			}
		}

		public override string PreferedJavaSdkPath { 
			get {
				var config_file = GetUnixConfigFile (Logger);
				var javaEl = config_file.Root.Element ("java-sdk");

				if (javaEl != null) {
					var path = (string)javaEl.Attribute ("path");

					if (ValidateJavaSdkLocation (path))
						return path;
				}
				return null;
			}
		}

		protected override IEnumerable<string> GetAllAvailableAndroidSdks ()
		{
			var preferedSdkPath = PreferedAndroidSdkPath;
			if (!string.IsNullOrEmpty (preferedSdkPath))
				yield return preferedSdkPath;

			// Look in PATH
			foreach (var adb in ProcessUtils.FindExecutablesInPath (Adb)) {
				var path = Path.GetDirectoryName (adb);
				// Strip off "platform-tools"
				var dir = Path.GetDirectoryName (path);

				if (ValidateAndroidSdkLocation (dir))
					yield return dir;
			}

			// Check some hardcoded paths for good measure
			var macSdkPath = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.Personal), "Library", "Android", "sdk");
			if (ValidateAndroidSdkLocation (macSdkPath))
				yield return macSdkPath;
		}

		protected override string GetJavaSdkPath ()
		{
			return JdkInfo.GetKnownSystemJdkInfos (Logger).FirstOrDefault ()?.HomePath;
		}

		protected override IEnumerable<string> GetAllAvailableAndroidNdks ()
		{
			var preferedNdkPath = PreferedAndroidNdkPath;
			if (!string.IsNullOrEmpty (preferedNdkPath))
				yield return preferedNdkPath;

			// Look in PATH
			foreach (var ndkStack in ProcessUtils.FindExecutablesInPath (NdkStack)) {
				var ndkDir  = Path.GetDirectoryName (ndkStack);
				if (ValidateAndroidNdkLocation (ndkDir))
					yield return ndkDir;
			}
		}

		protected override string GetShortFormPath (string path)
		{
			// This is a Windows-ism, don't do anything for Unix
			return path;
		}

		public override void SetPreferredAndroidSdkPath (string path)
		{
			path = NullIfEmpty (path);

			var doc = GetUnixConfigFile (Logger);
			var androidEl = doc.Root.Element ("android-sdk");

			if (androidEl == null) {
				androidEl = new XElement ("android-sdk");
				doc.Root.Add (androidEl);
			}

			androidEl.SetAttributeValue ("path", path);
			SaveConfig (doc);
		}

		public override void SetPreferredJavaSdkPath (string path)
		{
			path = NullIfEmpty (path);

			var doc = GetUnixConfigFile (Logger);
			var javaEl = doc.Root.Element ("java-sdk");

			if (javaEl == null) {
				javaEl = new XElement ("java-sdk");
				doc.Root.Add (javaEl);
			}

			javaEl.SetAttributeValue ("path", path);
			SaveConfig (doc);
		}

		public override void SetPreferredAndroidNdkPath (string path)
		{
			path = NullIfEmpty (path);

			var doc = GetUnixConfigFile (Logger);
			var androidEl = doc.Root.Element ("android-ndk");

			if (androidEl == null) {
				androidEl = new XElement ("android-ndk");
				doc.Root.Add (androidEl);
			}

			androidEl.SetAttributeValue ("path", path);
			SaveConfig (doc);
		}

		void SaveConfig (XDocument doc)
		{
			string cfg = UnixConfigPath;
			List <string> created = null;

			if (!File.Exists (cfg)) {
				string dir = Path.GetDirectoryName (cfg);
				if (!Directory.Exists (dir)) {
					Directory.CreateDirectory (dir);
					AddToList (dir);
				}
				AddToList (cfg);
			}
			doc.Save (cfg);
			FixOwnership (created);

			void AddToList (string path)
			{
				if (created == null)
					created = new List <string> ();
				created.Add (path);
			}
		}

		static  readonly    string  GetUnixConfigDirOverrideName            = $"UnixConfigPath directory override! {typeof (AndroidSdkInfo).AssemblyQualifiedName}";

		// There's a small problem with the code below. Namely, if it runs under `sudo` the folder location
		// returned by Environment.GetFolderPath will depend on how sudo was invoked:
		//
		//   1. `sudo command` will not reset the environment and while the user running the command will be
		//      `root` (or any other user specified in the command), the `$HOME` environment variable will point
		//      to the original user's home. The effect will be that any files/directories created in this
		//      session will be owned by `root` (or any other user as above) and not the original user. Thus, on
		//      return, the original user will not have write (or read/write) access to the directory/file
		//      created. This causes https://devdiv.visualstudio.com/DevDiv/_workitems/edit/597752
		//
		//   2. `sudo -i command` starts an "interactive" session which resets the environment (by reading shell
		//      startup scripts among other steps) and the above problem doesn't occur.
		//
		// The behavior of 1. is, arguably, a bug in Mono fixing of which may bring unknown side effects,
		// however. Therefore we'll do our best below to work around the issue. `sudo` puts the original user's
		// login name in the `SUDO_USER` environment variable and we can use its presence to both detect 1.
		// above and work around the issue. We will do it in the simplest possible manner, by invoking chown(1)
		// to set the proper ownership.
		// Note that it will NOT fix situations when a mechanism other than `sudo`, but with similar effects, is
		// used! The generic fix would require a number of more complicated checks as well as a number of
		// p/invokes (with quite a bit of data marshaling) and it is likely that it would be mostly wasted
		// effort, as the sudo situation appears to be the most common (while happening few and far between in
		// general)
		//
		private static string UnixConfigPath {
			get {
				var p   = AppDomain.CurrentDomain.GetData (GetUnixConfigDirOverrideName)?.ToString ();
				if (string.IsNullOrEmpty (p)) {
					p   = Environment.GetFolderPath (Environment.SpecialFolder.ApplicationData);
					p   = Path.Combine (p, "xbuild");
				}
				return Path.Combine (p, "monodroid-config.xml");
			}
		}

		internal static XDocument GetUnixConfigFile (Action<TraceLevel, string> logger)
		{
			var file = UnixConfigPath;
			XDocument doc = null;
			if (File.Exists (file)) {
				try {
					doc = XDocument.Load (file);
				} catch (Exception ex) {
					logger (TraceLevel.Error, "Could not load monodroid configuration file");
					logger (TraceLevel.Verbose, ex.ToString ());

					// move out of the way and create a new one
					doc = new XDocument (new XElement ("monodroid"));
					var newFileName = file + ".old";
					if (File.Exists (newFileName)) {
						File.Delete (newFileName);
					}

					File.Move (file, newFileName);
				}
			}

			if (doc == null || doc.Root == null) {
				doc = new XDocument (new XElement ("monodroid"));
			}
			return doc;
		}

		void FixOwnership (List<string> paths)
		{
			if (!need_chown || paths == null || paths.Count == 0)
				return;

			var stdout = new StringWriter ();
			var stderr = new StringWriter ();
			var args = new List <string> {
				QuoteString (sudo_user)
			};

			foreach (string p in paths)
				args.Add (QuoteString (p));

			var psi = new ProcessStartInfo (OS.IsMac ? "/usr/sbin/chown" : "/bin/chown") {
				CreateNoWindow = true,
				Arguments = String.Join (" ", args),
			};
			Logger (TraceLevel.Verbose, $"Changing filesystem object ownership: {psi.FileName} {psi.Arguments}");
			Task<int> chown_task = ProcessUtils.StartProcess (psi, stdout, stderr, System.Threading.CancellationToken.None);

			if (chown_task.Result != 0) {
				Logger (TraceLevel.Warning, $"Failed to change ownership of filesystem object(s)");
				Logger (TraceLevel.Verbose, $"standard output: {stdout}");
				Logger (TraceLevel.Verbose, $"standard error: {stderr}");
			}

			string QuoteString (string p)
			{
				return $"\"{p}\"";
			}
		}

	}
}

namespace Xamarin.Android.Tools
{
	class AndroidSdkWindows : AndroidSdkBase
	{
		const string MDREG_KEY = @"SOFTWARE\Novell\Mono for Android";
		const string MDREG_ANDROID_SDK = "AndroidSdkDirectory";
		const string MDREG_ANDROID_NDK = "AndroidNdkDirectory";
		const string MDREG_JAVA_SDK = "JavaSdkDirectory";
		const string ANDROID_INSTALLER_PATH = @"SOFTWARE\Android SDK Tools";
		const string ANDROID_INSTALLER_KEY = "Path";
		const string XAMARIN_ANDROID_INSTALLER_PATH = @"SOFTWARE\Xamarin\MonoAndroid";
		const string XAMARIN_ANDROID_INSTALLER_KEY = "PrivateAndroidSdkPath";

		public AndroidSdkWindows (Action<TraceLevel, string> logger)
			: base (logger)
		{
		}

		static readonly string _JarSigner = "jarsigner.exe";

		public override string ZipAlign { get; protected set; } = "zipalign.exe";
		public override string JarSigner { get; protected set; } = _JarSigner;
		public override string KeyTool { get; protected set; } = "keytool.exe";

		public override string NdkHostPlatform32Bit { get { return "windows"; } }
		public override string NdkHostPlatform64Bit { get { return "windows-x86_64"; } }
		public override string Javac { get; protected set; } = "javac.exe";

		public override string PreferedAndroidSdkPath {
			get {
				var wow = RegistryEx.Wow64.Key32;
				var regKey = GetMDRegistryKey ();
				if (CheckRegistryKeyForExecutable (RegistryEx.CurrentUser, regKey, MDREG_ANDROID_SDK, wow, "platform-tools", Adb))
					return RegistryEx.GetValueString (RegistryEx.CurrentUser, regKey, MDREG_ANDROID_SDK, wow);
				return null;
			}
		}
		public override string PreferedAndroidNdkPath {
			get {
				var wow = RegistryEx.Wow64.Key32;
				var regKey = GetMDRegistryKey ();
				if (CheckRegistryKeyForExecutable (RegistryEx.CurrentUser, regKey, MDREG_ANDROID_NDK, wow, ".", NdkStack))
					return RegistryEx.GetValueString (RegistryEx.CurrentUser, regKey, MDREG_ANDROID_NDK, wow);
				return null;
			}
		}
		public override string PreferedJavaSdkPath {
			get {
				var wow = RegistryEx.Wow64.Key32;
				var regKey = GetMDRegistryKey ();
				if (CheckRegistryKeyForExecutable (RegistryEx.CurrentUser, regKey, MDREG_JAVA_SDK, wow, "bin", JarSigner))
					return RegistryEx.GetValueString (RegistryEx.CurrentUser, regKey, MDREG_JAVA_SDK, wow);
				return null;
			}
		}

		static string GetMDRegistryKey ()
		{
			var regKey = Environment.GetEnvironmentVariable ("XAMARIN_ANDROID_REGKEY");
			return string.IsNullOrWhiteSpace (regKey) ? MDREG_KEY : regKey;
		}

		protected override IEnumerable<string> GetAllAvailableAndroidSdks ()
		{
			var roots = new[] { RegistryEx.CurrentUser, RegistryEx.LocalMachine };
			var wow = RegistryEx.Wow64.Key32;
			var regKey = GetMDRegistryKey ();

			Logger (TraceLevel.Info, "Looking for Android SDK...");

			// Check for the key the user gave us in the VS/addin options
			foreach (var root in roots)
				if (CheckRegistryKeyForExecutable (root, regKey, MDREG_ANDROID_SDK, wow, "platform-tools", Adb))
					yield return RegistryEx.GetValueString (root, regKey, MDREG_ANDROID_SDK, wow);

			// Check for the key written by the Xamarin installer
			if (CheckRegistryKeyForExecutable (RegistryEx.CurrentUser, XAMARIN_ANDROID_INSTALLER_PATH, XAMARIN_ANDROID_INSTALLER_KEY, wow, "platform-tools", Adb))
				yield return RegistryEx.GetValueString (RegistryEx.CurrentUser, XAMARIN_ANDROID_INSTALLER_PATH, XAMARIN_ANDROID_INSTALLER_KEY, wow);

			// Check for the key written by the Android SDK installer
			foreach (var root in roots)
				if (CheckRegistryKeyForExecutable (root, ANDROID_INSTALLER_PATH, ANDROID_INSTALLER_KEY, wow, "platform-tools", Adb))
					yield return RegistryEx.GetValueString (root, ANDROID_INSTALLER_PATH, ANDROID_INSTALLER_KEY, wow);

			// Check some hardcoded paths for good measure
			var paths = new string [] {
				Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData), "Xamarin", "MonoAndroid", "android-sdk-windows"),
				Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.ProgramFilesX86), "Android", "android-sdk"),
				Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.ProgramFilesX86), "Android", "android-sdk-windows"),
				!string.IsNullOrEmpty (Environment.GetEnvironmentVariable ("ProgramW6432"))
					? Path.Combine (Environment.GetEnvironmentVariable ("ProgramW6432"), "Android", "android-sdk")
					: Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.ProgramFiles), "Android", "android-sdk"),
				Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData), "Android", "android-sdk"),
				Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.CommonApplicationData), "Android", "android-sdk"),
				@"C:\android-sdk-windows"
			};
			foreach (var basePath in paths)
				if (Directory.Exists (basePath))
					if (ValidateAndroidSdkLocation (basePath))
						yield return basePath;
		}

		protected override string GetJavaSdkPath ()
		{
			var jdk = GetJdkInfos (Logger).FirstOrDefault ();
			return jdk?.HomePath;
		}

		internal static IEnumerable<JdkInfo> GetJdkInfos (Action<TraceLevel, string> logger)
		{
			JdkInfo TryGetJdkInfo (string path, string locator)
			{
				JdkInfo jdk = null;
				try {
					jdk = new JdkInfo (path, locator);
				}
				catch (Exception e) {
					logger (TraceLevel.Warning, $"Not a valid JDK directory: `{path}`; via category: {locator}");
					logger (TraceLevel.Verbose, e.ToString ());
				}
				return jdk;
			}

			IEnumerable<JdkInfo> ToJdkInfos (IEnumerable<string> paths, string locator)
			{
				return paths.Select (p => TryGetJdkInfo (p, locator))
					.Where (jdk => jdk != null)
					.OrderByDescending (jdk => jdk, JdkInfoVersionComparer.Default);
			}

			return ToJdkInfos (GetPreferredJdkPaths (), "Preferred Registry")
				.Concat (ToJdkInfos (GetOpenJdkPaths (), "OpenJDK"))
				.Concat (ToJdkInfos (GetKnownOpenJdkPaths (), "Well-known OpenJDK paths"))
				.Concat (ToJdkInfos (GetOracleJdkPaths (), "Oracle JDK"))
				.Concat (ToJdkInfos (GetEnvironmentJdkPaths (), "Environment Variables"));
		}

		private static IEnumerable<string> GetEnvironmentJdkPaths ()
		{
			var environment = new [] { "JAVA_HOME" };
			foreach (var key in environment) {
				var value = Environment.GetEnvironmentVariable (key);
				if (!string.IsNullOrEmpty (value)) {
					yield return value;
				}
			}
		}

		private static IEnumerable<string> GetPreferredJdkPaths ()
		{
			// check the user specified path
			var roots = new[] { RegistryEx.CurrentUser, RegistryEx.LocalMachine };
			const RegistryEx.Wow64 wow = RegistryEx.Wow64.Key32;
			var regKey = GetMDRegistryKey ();

			foreach (var root in roots) {
				if (CheckRegistryKeyForExecutable (root, regKey, MDREG_JAVA_SDK, wow, "bin", _JarSigner))
					yield return RegistryEx.GetValueString (root, regKey, MDREG_JAVA_SDK, wow);
			}
		}

		private static IEnumerable<string> GetOpenJdkPaths ()
		{
			var root = RegistryEx.LocalMachine;
			var wows = new[] { RegistryEx.Wow64.Key32, RegistryEx.Wow64.Key64 };
			var subKey = @"SOFTWARE\Microsoft\VisualStudio\Android";
			var valueName = "JavaHome";

			foreach (var wow in wows) {
				if (CheckRegistryKeyForExecutable (root, subKey, valueName, wow, "bin", _JarSigner))
					yield return RegistryEx.GetValueString (root, subKey, valueName, wow);
			}
		}

		/// <summary>
		/// Locate OpenJDK installations by well known path.
		/// </summary>
		/// <returns>List of valid OpenJDK paths in version descending order.</returns>
		private static IEnumerable<string> GetKnownOpenJdkPaths ()
		{
			string JdkFolderNamePattern = "microsoft_dist_openjdk_";

			var paths = new List<Tuple<string, Version>> ();
			var rootPaths = new List<string> {
				Path.Combine (Environment.ExpandEnvironmentVariables ("%ProgramW6432%"), "Android", "jdk"),
				Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.ProgramFilesX86), "Android", "jdk"),
			};

			foreach (var rootPath in rootPaths) {
				if (Directory.Exists (rootPath))  {
					foreach (var directoryName in Directory.EnumerateDirectories (rootPath, $"{JdkFolderNamePattern}*").ToList ()) {
						var versionString = directoryName.Replace ($"{rootPath}\\{JdkFolderNamePattern}", string.Empty);
						if (Version.TryParse (versionString, out Version ver)) {
							paths.Add (new Tuple<string, Version>(directoryName, ver));
						}
					}
				}
			}

			return paths.OrderByDescending (v => v.Item2)
				.Where (openJdk => ProcessUtils.FindExecutablesInDirectory (Path.Combine (openJdk.Item1, "bin"), _JarSigner).Any ())
				.Select (openJdk => openJdk.Item1);
		}

		private static IEnumerable<string> GetOracleJdkPaths ()
		{ 
			string subkey = @"SOFTWARE\JavaSoft\Java Development Kit";

			foreach (var wow64 in new[] { RegistryEx.Wow64.Key32, RegistryEx.Wow64.Key64 }) {
				string key_name = string.Format (@"{0}\{1}\{2}", "HKLM", subkey, "CurrentVersion");
				var currentVersion = RegistryEx.GetValueString (RegistryEx.LocalMachine, subkey, "CurrentVersion", wow64);

				if (!string.IsNullOrEmpty (currentVersion)) {

					// No matter what the CurrentVersion is, look for 1.6 or 1.7 or 1.8
					if (CheckRegistryKeyForExecutable (RegistryEx.LocalMachine, subkey + "\\" + "1.8", "JavaHome", wow64, "bin", _JarSigner))
						yield return RegistryEx.GetValueString (RegistryEx.LocalMachine, subkey + "\\" + "1.8", "JavaHome", wow64);

					if (CheckRegistryKeyForExecutable (RegistryEx.LocalMachine, subkey + "\\" + "1.7", "JavaHome", wow64, "bin", _JarSigner))
						yield return RegistryEx.GetValueString (RegistryEx.LocalMachine, subkey + "\\" + "1.7", "JavaHome", wow64);

					if (CheckRegistryKeyForExecutable (RegistryEx.LocalMachine, subkey + "\\" + "1.6", "JavaHome", wow64, "bin", _JarSigner))
						yield return RegistryEx.GetValueString (RegistryEx.LocalMachine, subkey + "\\" + "1.6", "JavaHome", wow64);
				}
			}
		}

		protected override IEnumerable<string> GetAllAvailableAndroidNdks ()
		{
			var roots = new[] { RegistryEx.CurrentUser, RegistryEx.LocalMachine };
			var wow = RegistryEx.Wow64.Key32;
			var regKey = GetMDRegistryKey ();

			Logger (TraceLevel.Info, "Looking for Android NDK...");

			// Check for the "ndk-bundle" directory inside the SDK directories
			string ndk;

			var sdks = GetAllAvailableAndroidSdks().ToList();
			if (!string.IsNullOrEmpty(AndroidSdkPath))
				sdks.Add(AndroidSdkPath);
	
			foreach(var sdk in sdks.Distinct())
				if (Directory.Exists(ndk = Path.Combine(sdk, "ndk-bundle")))
					if (ValidateAndroidNdkLocation(ndk))
						yield return ndk;

			// Check for the key the user gave us in the VS/addin options
			foreach (var root in roots)
				if (CheckRegistryKeyForExecutable (root, regKey, MDREG_ANDROID_NDK, wow, ".", NdkStack))
					yield return RegistryEx.GetValueString (root, regKey, MDREG_ANDROID_NDK, wow);

			/*
			// Check for the key written by the Xamarin installer
			if (CheckRegistryKeyForExecutable (RegistryEx.CurrentUser, XAMARIN_ANDROID_INSTALLER_PATH, XAMARIN_ANDROID_INSTALLER_KEY, wow, "platform-tools", Adb))
				yield return RegistryEx.GetValueString (RegistryEx.CurrentUser, XAMARIN_ANDROID_INSTALLER_PATH, XAMARIN_ANDROID_INSTALLER_KEY, wow);
			*/

			// Check some hardcoded paths for good measure
			var xamarin_private = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData), "Xamarin", "MonoAndroid");
			var vs_default = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.CommonApplicationData), "Microsoft", "AndroidNDK");
			var vs_default32bit = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.CommonApplicationData), "Microsoft", "AndroidNDK32");
			var vs_2017_default = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.CommonApplicationData), "Microsoft", "AndroidNDK64");
			var android_default = Path.Combine (OS.ProgramFilesX86, "Android");
			var cdrive_default = @"C:\";

			foreach (var basePath in new string [] {xamarin_private, android_default, vs_default, vs_default32bit, vs_2017_default, cdrive_default})
				if (Directory.Exists (basePath))
					foreach (var dir in Directory.GetDirectories (basePath, "android-ndk-r*"))
						if (ValidateAndroidNdkLocation (dir))
							yield return dir;
		}

		protected override string GetShortFormPath (string path)
		{
			return KernelEx.GetShortPathName (path);
		}

		public override void SetPreferredAndroidSdkPath (string path)
		{
			var regKey = GetMDRegistryKey ();
			RegistryEx.SetValueString (RegistryEx.CurrentUser, regKey, MDREG_ANDROID_SDK, path ?? "", RegistryEx.Wow64.Key32);
		}

		public override void SetPreferredJavaSdkPath (string path)
		{
			var regKey = GetMDRegistryKey ();
			RegistryEx.SetValueString (RegistryEx.CurrentUser, regKey, MDREG_JAVA_SDK, path ?? "", RegistryEx.Wow64.Key32);
		}

		public override void SetPreferredAndroidNdkPath (string path)
		{
			var regKey = GetMDRegistryKey ();
			RegistryEx.SetValueString (RegistryEx.CurrentUser, regKey, MDREG_ANDROID_NDK, path ?? "", RegistryEx.Wow64.Key32);
		}

		#region Helper Methods
		private static bool CheckRegistryKeyForExecutable (UIntPtr key, string subkey, string valueName, RegistryEx.Wow64 wow64, string subdir, string exe)
		{
			string key_name = string.Format (@"{0}\{1}\{2}", key == RegistryEx.CurrentUser ? "HKCU" : "HKLM", subkey, valueName);

			var path = NullIfEmpty (RegistryEx.GetValueString (key, subkey, valueName, wow64));

			if (path == null) {
				return false;
			}

			if (!ProcessUtils.FindExecutablesInDirectory (Path.Combine (path, subdir), exe).Any ()) {
				return false;
			}

			return true;
		}
		#endregion

		public override void Initialize (string androidSdkPath = null, string androidNdkPath = null, string javaSdkPath = null)
		{
			base.Initialize (androidSdkPath, androidNdkPath, javaSdkPath);

			var jdkPath = JavaSdkPath;
			if (!string.IsNullOrEmpty (jdkPath)) {
				var cur = Environment.GetEnvironmentVariable ("JAVA_HOME");
				if (!string.IsNullOrEmpty (cur))
					Environment.SetEnvironmentVariable ("JAVA_HOME", jdkPath);

				var javaBinPath = this.JavaBinPath;
				if (!string.IsNullOrEmpty (javaBinPath)) {
					var environmentPath = Environment.GetEnvironmentVariable ("PATH");
					if (!environmentPath.Contains (javaBinPath)) {
						var processPath = string.Concat (javaBinPath, Path.PathSeparator, environmentPath);
						Environment.SetEnvironmentVariable ("PATH", processPath);
					}
				}
			}
		}
	}
}
namespace Xamarin.Android.Tools
{
	public class AndroidSdkInfo
	{
		AndroidSdkBase sdk;

		public AndroidSdkInfo (Action<TraceLevel, string> logger = null, string androidSdkPath = null, string androidNdkPath = null, string javaSdkPath = null)
		{
			logger  = logger ?? DefaultConsoleLogger;

			sdk = CreateSdk (logger);
			sdk.Initialize (androidSdkPath, androidNdkPath, javaSdkPath);

			// shouldn't happen, in that sdk.Initialize() should throw instead
			if (string.IsNullOrEmpty (AndroidSdkPath))
				throw new InvalidOperationException ($"Could not determine Android SDK location. Please provide `{nameof (androidSdkPath)}`.");
			if (string.IsNullOrEmpty (JavaSdkPath))
				throw new InvalidOperationException ($"Could not determine Java SDK location. Please provide `{nameof (javaSdkPath)}`.");
		}

		static AndroidSdkBase CreateSdk (Action<TraceLevel, string> logger)
		{
			return OS.IsWindows
				? (AndroidSdkBase) new AndroidSdkWindows (logger)
				: (AndroidSdkBase) new AndroidSdkUnix (logger);
		}

		public IEnumerable<string> GetBuildToolsPaths (string preferredBuildToolsVersion)
		{
			if (!string.IsNullOrEmpty (preferredBuildToolsVersion)) {
				var preferredDir = Path.Combine (AndroidSdkPath, "build-tools", preferredBuildToolsVersion);
				if (Directory.Exists (preferredDir))
					return new[] { preferredDir }.Concat (GetBuildToolsPaths ().Where (p => p!= preferredDir));
			}
			return GetBuildToolsPaths ();
		}

		public IEnumerable<string> GetBuildToolsPaths ()
		{
			var buildTools  = Path.Combine (AndroidSdkPath, "build-tools");
			if (Directory.Exists (buildTools)) {
				var preview = Directory.EnumerateDirectories (buildTools)
					.Where(x => TryParseVersion (Path.GetFileName (x)) == null)
					.Select(x => x);

				foreach (var d in preview)
					yield return d;

				var sorted = from p in Directory.EnumerateDirectories (buildTools)
					let version = TryParseVersion (Path.GetFileName (p))
						where version != null
					orderby version descending
					select p;

				foreach (var d in sorted)
					yield return d;
			}
			var ptPath  = Path.Combine (AndroidSdkPath, "platform-tools");
			if (Directory.Exists (ptPath))
				yield return ptPath;
		}

		static Version TryParseVersion (string v)
		{
			Version version;
			if (Version.TryParse (v, out version))
				return version;
			return null;
		}

		public IEnumerable<AndroidVersion> GetInstalledPlatformVersions (AndroidVersions versions)
		{
			if (versions == null)
				throw new ArgumentNullException (nameof (versions));
			return versions.InstalledBindingVersions
				.Where (p => TryGetPlatformDirectoryFromApiLevel (p.Id, versions) != null) ;
		}

		public string GetPlatformDirectory (int apiLevel)
		{
			return GetPlatformDirectoryFromId (apiLevel.ToString ());
		}

		public string GetPlatformDirectoryFromId (string id)
		{
			return Path.Combine (AndroidSdkPath, "platforms", "android-" + id);
		}

		public string TryGetPlatformDirectoryFromApiLevel (string idOrApiLevel, AndroidVersions versions)
		{
			var id  = versions.GetIdFromApiLevel (idOrApiLevel);
			var dir = GetPlatformDirectoryFromId (id);

			if (Directory.Exists (dir))
				return dir;

			var level   = versions.GetApiLevelFromId (id);
			dir         = level.HasValue ? GetPlatformDirectory (level.Value) : null;
			if (dir != null && Directory.Exists (dir))
				return dir;

			return null;
		}

		public bool IsPlatformInstalled (int apiLevel)
		{
			return apiLevel != 0 && Directory.Exists (GetPlatformDirectory (apiLevel));
		}

		public string AndroidNdkPath {
			get { return sdk.AndroidNdkPath; }
		}

		public string AndroidSdkPath {
			get { return sdk.AndroidSdkPath; }
		}

		public string [] AllAndroidSdkPaths {
			get {
				return sdk.AllAndroidSdks ?? new string [0];
			}
		}

		public string JavaSdkPath {
			get { return sdk.JavaSdkPath; }
		}

		public string AndroidNdkHostPlatform {
			get { return sdk.NdkHostPlatform; }
		}

		public static void SetPreferredAndroidNdkPath (string path, Action<TraceLevel, string> logger = null)
		{
			logger  = logger ?? DefaultConsoleLogger;

			var sdk = CreateSdk (logger);
			sdk.SetPreferredAndroidNdkPath(path);
		}

		internal static void DefaultConsoleLogger (TraceLevel level, string message)
		{
			switch (level) {
			case TraceLevel.Error:
				Console.Error.WriteLine (message);
				break;
			default:
				Console.WriteLine ($"[{level}] {message}");
				break;
			}
		}

		public static void SetPreferredAndroidSdkPath (string path, Action<TraceLevel, string> logger = null)
		{
			logger  = logger ?? DefaultConsoleLogger;

			var sdk = CreateSdk (logger);
			sdk.SetPreferredAndroidSdkPath (path);
		}

		public static void SetPreferredJavaSdkPath (string path, Action<TraceLevel, string> logger = null)
		{
			logger  = logger ?? DefaultConsoleLogger;

			var sdk = CreateSdk (logger);
			sdk.SetPreferredJavaSdkPath (path);
		}

		public static void DetectAndSetPreferredJavaSdkPathToLatest (Action<TraceLevel, string> logger = null)
		{
			if (OS.IsWindows)
				throw new NotImplementedException ("Windows is not supported at this time.");

			logger          = logger ?? DefaultConsoleLogger;

			var latestJdk   = JdkInfo.GetMacOSMicrosoftJdks (logger).FirstOrDefault ();
			if (latestJdk == null)
				throw new NotSupportedException ("No Microsoft OpenJDK could be found.  Please re-run the Visual Studio installer or manually specify the JDK path in settings.");

			var sdk = CreateSdk (logger);
			sdk.SetPreferredJavaSdkPath (latestJdk.HomePath);
		}
	}
}
namespace Xamarin.Android.Tools
{
	public class AndroidVersions
	{
		List<AndroidVersion>                installedVersions = new List<AndroidVersion> ();

		public  IReadOnlyList<string>       FrameworkDirectories            { get; }
		public  AndroidVersion              MaxStableVersion                { get; private set; }
		public  AndroidVersion              MinStableVersion                { get; private set; }

		public  IReadOnlyList<AndroidVersion>       InstalledBindingVersions    { get; private set; }

		public AndroidVersions (IEnumerable<string> frameworkDirectories)
		{
			if (frameworkDirectories == null)
				throw new ArgumentNullException (nameof (frameworkDirectories));

			var dirs    = new List<string> ();

			foreach (var d in frameworkDirectories) {
				if (!Directory.Exists (d))
					throw new ArgumentException ($"`{d}` must be a directory!", nameof (frameworkDirectories));

				var dp  = d.TrimEnd (Path.DirectorySeparatorChar);
				var dn  = Path.GetFileName (dp);
				// In "normal" use, `dp` will contain e.g. `...\MonoAndroid\v1.0`.
				// We want the `MonoAndroid` dir, not the versioned dir.
				var p   = dn.StartsWith ("v", StringComparison.Ordinal) ? Path.GetDirectoryName (dp) : dp;
				dirs.Add (Path.GetFullPath (p));
			}

			dirs    = dirs.Distinct (StringComparer.OrdinalIgnoreCase)
				.ToList ();

			FrameworkDirectories    = new ReadOnlyCollection<string> (dirs);

			var versions = dirs.SelectMany (d => Directory.EnumerateFiles (d, "AndroidApiInfo.xml", SearchOption.AllDirectories))
				.Select (file => AndroidVersion.Load (file));

			LoadVersions (versions);
		}

		public AndroidVersions (IEnumerable<AndroidVersion> versions)
		{
			if (versions == null)
				throw new ArgumentNullException (nameof (versions));

			FrameworkDirectories    = new ReadOnlyCollection<string> (new string [0]);

			LoadVersions (versions);
		}

		void LoadVersions (IEnumerable<AndroidVersion> versions)
		{
			foreach (var version in versions) {
				installedVersions.Add (version);
				if (!version.Stable)
					continue;
				if (MaxStableVersion == null || (MaxStableVersion.TargetFrameworkVersion < version.TargetFrameworkVersion)) {
					MaxStableVersion    = version;
				}
				if (MinStableVersion == null || (MinStableVersion.TargetFrameworkVersion > version.TargetFrameworkVersion)) {
					MinStableVersion = version;
				}
			}

			InstalledBindingVersions    = new ReadOnlyCollection<AndroidVersion>(installedVersions);
		}

		public int? GetApiLevelFromFrameworkVersion (string frameworkVersion)
		{
			return installedVersions.FirstOrDefault (v => MatchesFrameworkVersion (v, frameworkVersion))?.ApiLevel ??
				KnownVersions.FirstOrDefault (v => MatchesFrameworkVersion (v, frameworkVersion))?.ApiLevel;
		}

		static bool MatchesFrameworkVersion (AndroidVersion version, string frameworkVersion)
		{
			return version.FrameworkVersion == frameworkVersion ||
				version.OSVersion == frameworkVersion;
		}

		public int? GetApiLevelFromId (string id)
		{
			return installedVersions.FirstOrDefault (v => MatchesId (v, id))?.ApiLevel ??
				KnownVersions.FirstOrDefault (v => MatchesId (v, id))?.ApiLevel;
		}

		static bool MatchesId (AndroidVersion version, string id)
		{
			return version.Id == id ||
				(version.AlternateIds?.Contains (id) ?? false) ||
				(version.ApiLevel.ToString () == id);
		}

		public string GetIdFromApiLevel (int apiLevel)
		{
			return installedVersions.FirstOrDefault (v => v.ApiLevel == apiLevel)?.Id ??
				KnownVersions.FirstOrDefault (v => v.ApiLevel == apiLevel)?.Id;
		}

		// Sometimes, e.g. when new API levels are introduced, the "API level" is a letter, not a number,
		// e.g. 'API-H' for API-11, 'API-O' for API-26, etc.
		public string GetIdFromApiLevel (string apiLevel)
		{
			if (int.TryParse (apiLevel, out var platform))
				return GetIdFromApiLevel (platform);
			return installedVersions.FirstOrDefault (v => MatchesId (v, apiLevel))?.Id ??
				KnownVersions.FirstOrDefault (v => MatchesId (v, apiLevel))?.Id;
		}

		public string GetIdFromFrameworkVersion (string frameworkVersion)
		{
			return installedVersions.FirstOrDefault (v => MatchesFrameworkVersion (v, frameworkVersion))?.Id ??
				KnownVersions.FirstOrDefault (v => MatchesFrameworkVersion (v, frameworkVersion))?.Id;
		}

		public string GetFrameworkVersionFromApiLevel (int apiLevel)
		{
			return installedVersions.FirstOrDefault (v => v.ApiLevel == apiLevel)?.FrameworkVersion ??
				KnownVersions.FirstOrDefault (v => v.ApiLevel == apiLevel)?.FrameworkVersion;
		}

		public string GetFrameworkVersionFromId (string id)
		{
			return installedVersions.FirstOrDefault (v => MatchesId (v, id))?.FrameworkVersion ??
				KnownVersions.FirstOrDefault (v => MatchesId (v, id))?.FrameworkVersion;
		}

		public static readonly AndroidVersion [] KnownVersions = new [] {
			new AndroidVersion (4,  "1.6",   "Donut"),
			new AndroidVersion (5,  "2.0",   "Eclair"),
			new AndroidVersion (6,  "2.0.1", "Eclair"),
			new AndroidVersion (7,  "2.1",   "Eclair"),
			new AndroidVersion (8,  "2.2",   "Froyo"),
			new AndroidVersion (10, "2.3",   "Gingerbread"),
			new AndroidVersion (11, "3.0",   "Honeycomb") {
				AlternateIds = new[]{ "H" },
			},
			new AndroidVersion (12, "3.1",   "Honeycomb"),
			new AndroidVersion (13, "3.2",   "Honeycomb"),
			new AndroidVersion (14, "4.0",   "Ice Cream Sandwich"),
			new AndroidVersion (15, "4.0.3", "Ice Cream Sandwich"),
			new AndroidVersion (16, "4.1",   "Jelly Bean"),
			new AndroidVersion (17, "4.2",   "Jelly Bean"),
			new AndroidVersion (18, "4.3",   "Jelly Bean"),
			new AndroidVersion (19, "4.4",   "Kit Kat"),
			new AndroidVersion (20, "4.4.87", "Kit Kat + Wear support"),
			new AndroidVersion (21, "5.0",   "Lollipop") {
				AlternateIds = new[]{ "L" },
			},
			new AndroidVersion (22, "5.1",   "Lollipop"),
			new AndroidVersion (23, "6.0",   "Marshmallow") {
				AlternateIds = new[]{ "M" },
			},
			new AndroidVersion (24, "7.0",   "Nougat") {
				AlternateIds = new[]{ "N" },
			},
			new AndroidVersion (25, "7.1",   "Nougat"),
			new AndroidVersion (26, "8.0",   "Oreo") {
				AlternateIds = new[]{ "O" },
			},
			new AndroidVersion (27, "8.1",   "Oreo"),
			new AndroidVersion (28, "9.0",   "Pie") {
				AlternateIds = new[]{ "P" },
			},
		};
	}

	class EqualityComparer<T> : IEqualityComparer<T>
	{
		Func<T, T, bool>    equals;
		Func<T, int>        getHashCode;

		public EqualityComparer (Func<T, T, bool> equals, Func<T, int> getHashCode = null)
		{
			this.equals         = equals;
			this.getHashCode    = getHashCode ?? (v => v.GetHashCode ());
		}

		public bool Equals (T x, T y)
		{
			return equals (x, y);
		}

		public int GetHashCode (T obj)
		{
			return getHashCode (obj);
		}
	}
}
namespace Xamarin.Android.Tools
{
	public class AndroidVersion
	{
		// Android API Level. *Usually* corresponds to $(AndroidSdkPath)/platforms/android-$(ApiLevel)/android.jar
		public  int             ApiLevel                { get; private set; }

		// Android API Level ID. == ApiLevel on stable versions, will be e.g. `N` for previews: $(AndroidSdkPath)/platforms/android-N/android.jar
		public  string          Id                      { get; private set; }

		// Name of an Android release, e.g. "Oreo"
		public  string          CodeName                { get; private set; }

		// Android version number, e.g. 8.0
		public string           OSVersion               { get; private set; }

		// Xamarin.Android $(TargetFrameworkVersion) value, e.g. 8.0
		public Version          TargetFrameworkVersion  { get; private set; }

		// TargetFrameworkVersion *with* a leading `v`, e.g. "v8.0"
		public string           FrameworkVersion        { get; private set; }

		// Is this API level stable? Should be False for non-numeric Id values.
		public bool             Stable                  { get; private set; }

		// Alternate Ids for a given API level. Allows for historical mapping, e.g. API-11 has alternate ID 'H'.
		internal    string[]    AlternateIds            { get; set; }

		public AndroidVersion (int apiLevel, string osVersion, string codeName = null, string id = null, bool stable = true)
		{
			if (osVersion == null)
				throw new ArgumentNullException (nameof (osVersion));

			ApiLevel                = apiLevel;
			Id                      = id ?? ApiLevel.ToString ();
			CodeName                = codeName;
			OSVersion               = osVersion;
			TargetFrameworkVersion  = Version.Parse (osVersion);
			FrameworkVersion        = "v" + osVersion;
			Stable                  = stable;
		}

		public override string ToString ()
		{
			return $"(AndroidVersion: ApiLevel={ApiLevel} Id={Id} OSVersion={OSVersion} CodeName='{CodeName}' TargetFrameworkVersion={TargetFrameworkVersion} Stable={Stable})";
		}

		public static AndroidVersion Load (Stream stream)
		{
			var doc = XDocument.Load (stream);
			return Load (doc);
		}

		public static AndroidVersion Load (string uri)
		{
			var doc = XDocument.Load (uri);
			return Load (doc);
		}

		// Example:
		// <AndroidApiInfo>
		//   <Id>26</Id>
		//   <Level>26</Level>
		//   <Name>Oreo</Name>
		//   <Version>v8.0</Version>
		//   <Stable>True</Stable>
		// </AndroidApiInfo>
		static AndroidVersion Load (XDocument doc)
		{
			var id      = (string) doc.Root.Element ("Id");
			var level   = (int) doc.Root.Element ("Level");
			var name    = (string) doc.Root.Element ("Name");
			var version = (string) doc.Root.Element ("Version");
			var stable  = (bool) doc.Root.Element ("Stable");

			return new AndroidVersion (level, version.TrimStart ('v'), name, id, stable);
		}
	}
}
