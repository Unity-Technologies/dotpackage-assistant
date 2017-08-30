using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using UnityEngine;
using UnityEditor;
using UnityEditor.VersionControl;

// See http://www.gzip.org/zlib/rfc-gzip.html#header-trailer for info on how gzip files are supposed to look like
[StructLayout(LayoutKind.Sequential)]
unsafe struct GzipHeader
{
	public byte id1;
	public byte id2;
	public byte compressionMethod;
	public byte flags; //1 bit each for FTEXT, FHCRC, FEXTRA, FNAME and three reamining bits are reserved
	public fixed byte mtime[4];
	public byte compressionLevel;
	public byte os;
};

struct GzipHeaderExtraField
{
	public byte id1;
	public byte id2;
	public UInt16 length;  //65 max - data larger than this will be in multiple extradata fields and should be concatenated

	public GzipHeaderExtraField(byte in_id1, byte in_id2, UInt16 in_length)
	{
		id1 = in_id1;
		id2 = in_id2;
		length = in_length;
	}
};

[StructLayout(LayoutKind.Sequential)]
unsafe struct TarHeader
{
	public fixed byte filename[100];
	public fixed byte filemode[8];
	public fixed byte uid[8];
	public fixed byte gid[8];
	public fixed byte filesize[12]; //Octal
	public fixed byte modtime[12];
	public fixed byte checksum[8];
	public byte linkIndicator;      //OR Type flag in UStar format
	public fixed byte linkname[100];
	//USTAR fields
	public fixed byte ustar[6];
	public fixed byte ustarVersion[2];
	public fixed byte uname[32];
	public fixed byte ugroupname[32];
	public fixed byte major[8];
	public fixed byte minor[8];
	public fixed byte prefix[155];
};

public struct PackageEntry
{
	public string pathname;     //Name of the asset once extracted
	public bool hasMeta;        //Should be true for all package entries
	public bool hasAsset;       //Should be true for assets, but not for directories
}

class PackageManifestEntryComparer : IEqualityComparer<PackageManifestEntry>
{
	public bool Equals(PackageManifestEntry x, PackageManifestEntry y)
	{
		return (x.filepath == y.filepath && x.sha1Hash == y.sha1Hash && x.modTime == y.modTime && x.fileSize == y.fileSize);
	}

	public int GetHashCode(PackageManifestEntry entry)
	{
		if (object.ReferenceEquals(entry, null))
			return 0;
		int hashFilepath = entry.filepath == null ? 0 : entry.filepath.GetHashCode();
		int hashSha1Hash = entry.sha1Hash == null ? 0 : entry.sha1Hash.GetHashCode();
		return hashFilepath ^ hashSha1Hash ^ entry.modTime.GetHashCode() ^ entry.fileSize.GetHashCode();
	}
}

[Serializable]
public struct PackageManifestEntry
{
	public string filepath;    //relative from project root - lowercased, forward-slashed and leading slashes removed
	public string sha1Hash;    //sha1 hash of the file's contents - we may want this if we want to enumerate the files that have been modified since installation
	public UInt64 modTime;   //File's mtime on disk - used for broad phase change detection
	public UInt64 fileSize; //File size - used for broad phase change detection

	public PackageManifestEntry(string in_filepath, bool calcHash = false)
	{
		sha1Hash = string.Empty;
		filepath = in_filepath;
		FileInfo info = new FileInfo(filepath);
		modTime = Convert.ToUInt64(info.LastWriteTimeUtc.ToFileTimeUtc());
		fileSize = Convert.ToUInt64(info.Length);
		if (calcHash)
			CalculateHash();
	}

	public void CalculateHash()
	{
		//TODO: Reduce memory pressure by reading in a fixed size buffer at a time - these files could be enormous
		byte[] allBytes = File.ReadAllBytes(filepath);
		SHA1 sha1 = SHA1.Create();
		sha1Hash = BitConverter.ToString(sha1.ComputeHash(allBytes)).Replace("-","");
	}
}

[Serializable]
public struct ProjectState
{
	public List<PackageManifestEntry> state;

	public ProjectState(List<PackageManifestEntry> in_state)
	{
		state = in_state;
	}
}

[Serializable]
public class PackageManifest
{
	public string title;
	public string metadata;
	//This represents the canonical filelist - the set of files that are contained in the package
	public List<string> filelistComplete;
	//The complete filelist above isn't necessarily what gets installed. Users can elect not to install certain files
	//If a package contains scripts (many do) these can be modified by Unity at import time (to update their APIs) and may
	//spawn other scripts or files during package import. This list can be a subset or a superset of the package files as
	//it can contain files that 'appear' during the installation and import process (excluding OS temp files e.g. .DS_Store).
	public List<PackageManifestEntry> filelistInstalled;
	//Note that the filelist in the package is 'just' a list of strings representing file paths; we don't actually care about
	//the contents of the files in the package (as they can be changed during install)
	//TODO: If we want to extend this script to diff packages, we will need to understand file contents

	public PackageManifest(string in_title, string meta, List<string> files)
	{
		title = in_title;
		metadata = meta;
		filelistComplete = files;
	}
};

public class DotPackageAssistantWindow : EditorWindow
{
	//Debugging
	string packageManifestPath = "PackageManifests";
	string packageManifestTempFile = "tempPackageManifest";
	string preinstallStateTempFile = "tempPreInstallState";
	AssetList toReAdd = new AssetList();
	PackageManifest manifestToInstall = null;

	public string packageTempManifest
	{
		get
		{
			return Path.Combine(Application.temporaryCachePath, packageManifestTempFile);
		}
	}

	public string preinstallTempState
	{
		get
		{
			return Path.Combine(Application.temporaryCachePath, preinstallStateTempFile);
		}
	}

	[MenuItem("Window/DotPackage Assistant")]
	public static void ShowWindow()
	{
		//Show existing window instance. If one doesn't exist, make one.
		EditorWindow.GetWindow(typeof(DotPackageAssistantWindow));
	}

	void OnGUI()
	{
		GUILayout.Label("Base Settings", EditorStyles.boldLabel);
		packageManifestPath = EditorGUILayout.TextField("Package Manifest Directory", packageManifestPath);
		string packageDir = string.Empty;
		if (Directory.Exists("Packages"))
			packageDir = @"PackageFiles";

		GUILayout.Label("Package Management Operations", EditorStyles.boldLabel);
		if (GUILayout.Button("Uninstall Package"))
		{
			string manifestPath = EditorUtility.OpenFilePanel("Select Package", packageManifestPath, "manifest");
			if (string.IsNullOrEmpty(manifestPath) == false)
				UninstallPackage(manifestPath);
		}
		if (GUILayout.Button("Install Package"))
		{
			string packagePath = EditorUtility.OpenFilePanel("Select Package", packageDir, "unitypackage");
			if (string.IsNullOrEmpty(packagePath) == false)
				InstallPackage(packagePath);
		}
		if (File.Exists(packageTempManifest))
		{
			if (GUILayout.Button("Finish Package Installation"))
			{
				FinishManifestInstallation();
			}
		}

		GUILayout.Label("Debug Operations", EditorStyles.boldLabel);
		if (GUILayout.Button("Extract Manifest for installed package"))
		{
			string packagePath = EditorUtility.OpenFilePanel("Select Package", packageDir, "unitypackage");
			if (string.IsNullOrEmpty(packagePath) == false)
				InstallManifest(ExtractPackageManifest(packagePath));
		}
		if (GUILayout.Button("Purge Temp Files"))
		{
			PurgeTempFiles();
		}
	}

	public void PurgeTempFiles()
	{
		if (File.Exists(packageTempManifest))
			File.Delete(packageTempManifest);
		if (File.Exists(preinstallTempState))
			File.Delete(preinstallTempState);
	}

	void FinishManifestInstallation()
	{
		PackageManifest manifest = (PackageManifest)JsonUtility.FromJson(File.ReadAllText(packageTempManifest), typeof(PackageManifest));
		ProjectState preInstallState = (ProjectState)JsonUtility.FromJson(File.ReadAllText(preinstallTempState), typeof(ProjectState));
		List<PackageManifestEntry> postInstallState = new List<PackageManifestEntry>();
		GatherProjectDirState("assets", ref postInstallState);
		postInstallState.Sort((x, y) => x.filepath.CompareTo(y.filepath));

		//Diff the project directory states to see what's changed - this represents the install list for this package
		manifest.filelistInstalled = postInstallState.Except(preInstallState.state, new PackageManifestEntryComparer()).ToList();

		InstallManifest(manifest);
		PurgeTempFiles();
	}

	void GatherProjectDirState(string directoryPath, ref List<PackageManifestEntry> state)
	{
		RegexOptions opts = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
		Regex[] includeDirectories = new Regex[] { new Regex(@"\Aassets/*", opts) };
		Regex[] ignoreFiles = new Regex[] { new Regex(@"\.DS_Store", opts), new Regex(@"desktop\.ini", opts) };
		foreach (string file in Directory.GetFiles(directoryPath))
		{
			string fileFmt = FormatPath(file);
			if (ignoreFiles.Any(x => x.IsMatch(fileFmt)) == false)
			{
				state.Add(new PackageManifestEntry(FormatPath(fileFmt)));
			}
		}
		foreach (string subdir in Directory.GetDirectories(directoryPath))
		{
			string subdirFmt = FormatPath(subdir);
			if (includeDirectories.Any(x => x.IsMatch(subdirFmt)) == true)
			{
				GatherProjectDirState(subdirFmt, ref state);
			}
		}
	}

	void InstallManifest(PackageManifest packageManifest)
	{
		if (Directory.Exists(packageManifestPath) == false)
			Directory.CreateDirectory(packageManifestPath);
		string manifestFilePath = Path.Combine(packageManifestPath, packageManifest.title) + ".manifest";

		if (Provider.isActive)
		{
			Asset manifestAsset = new Asset(manifestFilePath);
			Task statusTask = Provider.Status(manifestAsset);
			statusTask.Wait();
			AssetList manifestAssetList = statusTask.assetList;
			manifestAsset = manifestAssetList[0];

			if (File.Exists(manifestFilePath) == true)
			{
				if (Provider.IsOpenForEdit(manifestAssetList[0]) == false)
				{
					Task checkoutTask = Provider.Checkout(manifestAssetList, CheckoutMode.Asset);
					checkoutTask.Wait();
					if (checkoutTask.success == false)
						throw new Exception(string.Format("Failed to check out manifest file {0}", manifestFilePath));
				}
			}
		}

		//If there's an existing manifest at this point, merge in the installed files from previous runs.
		//Since you can install a package multiple times with different subsets of the files, we need to aggregate the results of the separate
		//installations
		if (File.Exists(manifestFilePath) == true)
		{
			PackageManifest oldManifest = (PackageManifest)JsonUtility.FromJson(File.ReadAllText(manifestFilePath), typeof(PackageManifest));
			packageManifest.filelistInstalled.AddRange(oldManifest.filelistInstalled.Except(packageManifest.filelistInstalled, new PackageManifestEntryComparer()));
		}

		File.WriteAllText(manifestFilePath, JsonUtility.ToJson(packageManifest));

		if (Provider.isActive)
		{
			Asset manifestAsset = new Asset(manifestFilePath);
			Task statusTask = Provider.Status(manifestAsset);
			statusTask.Wait();
			AssetList manifestAssetList = statusTask.assetList;
			manifestAsset = manifestAssetList[0];
			if (manifestAsset.isInCurrentProject == false)
			{
				Task addTask = Provider.Add(manifestAssetList, false);
				addTask.Wait();
				if (addTask.success == false)
					throw new Exception(string.Format("Failed to add manifest file {0} to version control", manifestFilePath));
			}
		}
	}

	PackageManifest ExtractPackageManifest(string packageFilePath)
	{
		//Extract the package info from the gzip header extra data
		string packageInfoJSON = ExtractPackageInfo(packageFilePath);
		string title;
		if (string.IsNullOrEmpty(packageInfoJSON))
		{
			title = Path.GetFileNameWithoutExtension(packageFilePath);
		}
		else
		{
			Regex titleRegex = new Regex("\"title\":\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
			Match titleMatch = titleRegex.Match(packageInfoJSON);
			if (titleMatch.Success == false)
				throw new Exception(string.Format("Couldn't find a title string in \"{0}\"", packageInfoJSON));
			title = titleMatch.Groups[1].Value;
		}
		//Got the package info (hopefullt) now get the filenames
		List<string> filenames = ExtractFileNames(packageFilePath);
		return new PackageManifest(title, packageInfoJSON, filenames);
	}

	List<PackageManifest> ReadInstalledPackageManifests()
	{
		List<PackageManifest> manifests = new List<PackageManifest>();
		if(Directory.Exists(packageManifestPath))
		{
			foreach (string file in Directory.GetFiles(packageManifestPath, "*.manifest"))
			{
				manifests.Add((PackageManifest)JsonUtility.FromJson(File.ReadAllText(file), typeof(PackageManifest)));
			}
		}
		return manifests;
	}

	string FormatPath(string path)
	{
		string outPath = path.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLower().TrimStart(new char[] { '/' });
		if (outPath.StartsWith("./"))
			outPath = outPath.Substring(2);
		return outPath;
	}

	void ListOverlappingPackagesAndFiles(List<PackageManifest> installedPackages, PackageManifest packageToTest, ref List<string> overlappingPackages, ref List<string> overlappingOtherFiles)
	{
		foreach (PackageManifest installedPackage in installedPackages)
		{
			if (installedPackage.filelistComplete.Intersect(packageToTest.filelistComplete).ToList().Count > 0)
			{
				overlappingPackages.Add(installedPackage.title);
			}
		}

		//Glob all the assets
		List<string> filesInProject = new List<string>();
		Queue<string> dirsToCheck = new Queue<string>();
		dirsToCheck.Enqueue("Assets");
		while (dirsToCheck.Count > 0)
		{
			string dirToCheck = dirsToCheck.Dequeue();
			foreach (string subDir in Directory.GetDirectories(dirToCheck))
			{
				dirsToCheck.Enqueue(subDir);
			}
			foreach (string file in Directory.GetFiles(dirToCheck))
			{
				filesInProject.Add(FormatPath(file));
			}
		}
		filesInProject.Sort();
		//Strip out all the ones we know to be in packages
		foreach (PackageManifest installedPackage in installedPackages)
		{
			filesInProject = filesInProject.Except(installedPackage.filelistComplete).ToList();
		}

		//filesInProject should just be a list of loose (not-from-packages) files now - see if any of them intersect with the package we're checking
		overlappingOtherFiles = filesInProject.Intersect(packageToTest.filelistComplete).ToList();
	}

	void importPackageCancelledCallback(string packageName)
	{
		Debug.LogWarning("Package import was cancelled - please fix any pending Version Control changes where used");
		RemoveCallbacks();
        PurgeTempFiles();
	}

	void importPackageFailedCallback(string packageName, string errorMessage)
	{
		Debug.LogErrorFormat("Package import failed - please revert any pending changes this script has made in Version Control where user. Error message: {0}", errorMessage);
        RemoveCallbacks();
        PurgeTempFiles();
	}

	void importPackageCompletedCallback(string packageName)
	{
        RemoveCallbacks();
		//If we're lucky we've avoided a domain reload and can just install the manifest as it's still in memory
		if (EditorUtility.DisplayDialog("Installation Successful", "Package installation complete. Install the package manifest now?", "Install", "Cancel"))
		{
			FinishManifestInstallation();
		}
	}

	void importPackageStartedCallback(string packageName)
	{
		//Annoyingly, package installation can cause a domain reload if the package contains scripts. So for now, serialise the manifest
		//out to a temporary file and tell the user they may need to finalise the process by hand
		EditorUtility.DisplayDialog("About to Install", "After Installation is complete you may need to reopen this package assistant and press the \"Install Manifest\" button to finalise installation", "Understood", string.Empty);
		File.WriteAllText(packageTempManifest, JsonUtility.ToJson(manifestToInstall));
		List<PackageManifestEntry> currentStateEntries = new List<PackageManifestEntry>();
		GatherProjectDirState("assets", ref currentStateEntries);
		ProjectState currentState = new ProjectState(currentStateEntries);
		File.WriteAllText(preinstallTempState, JsonUtility.ToJson(currentState));
	}

	void explodePath(string path, ref List<string> paths)
	{
		char[] separators = new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
		string[] splits = path.Split(separators, StringSplitOptions.RemoveEmptyEntries);
		string pathPart = string.Empty;
		foreach (string split in splits)
		{
			if (string.IsNullOrEmpty(pathPart) == false)
				pathPart += Path.AltDirectorySeparatorChar;
			pathPart += split;
			if (paths.Contains(pathPart) == false)
				paths.Add(pathPart);
		}
	}

	void UninstallPackage(string manifestPath)
	{
		PackageManifest manifest = (PackageManifest)JsonUtility.FromJson(File.ReadAllText(manifestPath), typeof(PackageManifest));
		List<string> directoriesToCheck = new List<string>();
		List<string> filesToCheck = manifest.filelistComplete;	//Use the full filelist when we don't have any info as to what was installed
		if (manifest.filelistInstalled.Count > 0)
		{
			filesToCheck = new List<string>();
			foreach (PackageManifestEntry entry in manifest.filelistInstalled)
			{
				filesToCheck.Add(entry.filepath);
			}
		}

		foreach (string file in filesToCheck)
		{
			if (file.EndsWith(".meta", StringComparison.InvariantCultureIgnoreCase) == true)
			{
				string fileNoExt = file.Substring(0, file.Length - 5);
				if (Directory.Exists(fileNoExt))
				{
					explodePath(fileNoExt, ref directoriesToCheck);
				}
			}
			else if(File.Exists(file))
				//Only delete non-meta-files - the .meta files go along with their associated asset anyway
				AssetDatabase.DeleteAsset(file);
		}

		//If we've left any directories, clear any empties
		AssetList dirMetasToDelete = new AssetList();
		List<string> dirsToDelete = new List<string>();
		directoriesToCheck.Sort();
		directoriesToCheck.Reverse();
		foreach (string dirToCheck in directoriesToCheck)
		{
			string[] files = Directory.GetFiles(dirToCheck);
			List<string> dirs = new List<string>();
			foreach (string dir in Directory.GetDirectories(dirToCheck))
				dirs.Add(FormatPath(dir));
			List<string> dirsLessDeleted = dirs.Except(dirsToDelete).ToList();
			List<string> legitFiles = new List<string>();
			foreach (string file in files)
			{
				if (file.EndsWith(".meta", StringComparison.InvariantCultureIgnoreCase) == false
					&& file.EndsWith(".DS_Store", StringComparison.InvariantCultureIgnoreCase) == false)
					legitFiles.Add(file);
			}

			//Mark a directory for delete if it contains no directories not already marked for delete and no non-meta, non-temp files files
			if (legitFiles.Count == 0 && dirsLessDeleted.Count == 0)
			{
				dirMetasToDelete.Add(new Asset(dirToCheck + ".meta"));
				dirsToDelete.Add(dirToCheck);
			}
		}

		if (Provider.isActive)
		{
			Task statusTask = Provider.Status(dirMetasToDelete);
			statusTask.Wait();
			if (statusTask.success == false)
				throw new Exception("Failed to get VCS status for one or more directory meta files - please see console for details");
			Task deleteTask = Provider.Delete(statusTask.assetList);
			deleteTask.Wait();
			if (deleteTask.success == false)
				throw new Exception("Failed to open for delete one or more directory meta files - please see console for details");
		}
		else
		{
			foreach (Asset dirMeta in dirMetasToDelete)
				File.Delete(dirMeta.fullName);
		}
		foreach (string dir in dirsToDelete)
			FileUtil.DeleteFileOrDirectory(dir);

		//Delete the manifest
		if (Provider.isActive)
		{
			Asset manifestAsset = new Asset(manifestPath);
			Task statusTask = Provider.Status(manifestAsset);
			statusTask.Wait();
			if (statusTask.success == false)
				throw new Exception(string.Format("Failed to get VCS status for {0} - please manually remove it from Version Control and your Project Directory", manifestPath));
			Task deleteTask = Provider.Delete(statusTask.assetList);
			deleteTask.Wait();
			if (deleteTask.success == false)
				throw new Exception(string.Format("Failed to open {0} for delete - please manually remove it from Version Control and your Project Directory", manifestPath));
		}
		else
		{
			File.Delete(manifestPath);
		}
		EditorUtility.DisplayDialog("Uninstall Complete", string.Format("Uninstalled package {0}", manifest.title), "OK", string.Empty);
	}

	void InstallPackage(string packageFilePath)
	{
		PackageManifest manifest = ExtractPackageManifest(packageFilePath);
		//File.WriteAllText("testoutput.json", JsonUtility.ToJson(manifest, /*prettyPrint=*/true));
		List<PackageManifest> installedPackages = ReadInstalledPackageManifests();
		List<string> overlappingPackages = new List<string>();
		List<string> overlappingLooseFiles = new List<string>();
		ListOverlappingPackagesAndFiles(installedPackages, manifest, ref overlappingPackages, ref overlappingLooseFiles);
		if (overlappingPackages.Any(x => x == manifest.title))
		{
			if (EditorUtility.DisplayDialog("Warning", String.Format("Package {0} is already present in your installed packages list - the manifest will be updated with any changes you make", manifest.title), "OK", "Cancel") == false)
				return;
			overlappingPackages.Remove(manifest.title);
		}
		if (overlappingPackages.Count > 0)
		{
			foreach (string overlappingPackage in overlappingPackages)
				Debug.LogErrorFormat("Package {0} overlaps with the contents of package {1}", overlappingPackage, packageFilePath);
			if (EditorUtility.DisplayDialog("Error", "The requested package overlaps one or more installed packages (see console for list) - you should uninstall these first!", "Install Anyway", "Cancel") == false)
			{
				return;
			}
		}
		if (overlappingLooseFiles.Count > 0)
		{
			foreach (string overlappingFile in overlappingLooseFiles)
				Debug.LogWarningFormat("File {0} will be replaced during package installation", overlappingFile);
			if (EditorUtility.DisplayDialog("Warning", "The requested package will replace files in the project (see console for list). Proceed?", "Install Anyway", "Cancel") == false)
			{
				return;
			}
		}

		//From this point we don't care if a file being overwritten by this package is from another package or untracked
		//We also can't tell, with the current state of the API, what files the user is going to elect to install from the package
		//So if they're tracked in VCS, they all need to be opened for edit before installation.
		//New files are automatically added by Unity - we may need to re-add them though if they've been marked for delete

		toReAdd = new AssetList();
		if (Provider.isActive)
		{
			AssetList All = new AssetList();
			foreach (string packageFile in manifest.filelistComplete)
			{
				All.Add(new Asset(packageFile));
			}
			Task statusTask = Provider.Status(All);
			statusTask.Wait();
			if (statusTask.success == false)
				throw new Exception("Failed to query VCS status of files in the package");
			AssetList toOpen = new AssetList();
			bool bailOut = false;
			foreach (Asset asset in statusTask.assetList)
			{
				//NO COMMIT - Test what happens if deleted local or remote
				if (asset.IsOneOfStates(new Asset.States[] { Asset.States.DeletedLocal, Asset.States.DeletedRemote }))
					toReAdd.Add(asset);
				else if (asset.IsOneOfStates(new Asset.States[] { Asset.States.AddedLocal, Asset.States.CheckedOutLocal }) == false)
					toOpen.Add(asset);
				if (asset.IsOneOfStates(new Asset.States[] { Asset.States.LockedRemote }))
				{
					bailOut = true;
					Debug.LogError(string.Format("File {0} cannot be opened because it is locked remotely", asset.fullName));
				}
			}
			if (bailOut)
				//TODO: Message Box instead?
				throw new Exception("There was one or more fatal Version Control issues when installing your package - see console for details");

			if (toOpen.Count > 0)
			{
				Task openTask = Provider.Checkout(toOpen, CheckoutMode.Exact);
				openTask.Wait();
				if (openTask.success == false)
					throw new Exception("Failed to open files for edit before installing package. Please see console for details from the VCS plugin.");
			}
		}

		//Install the package after serialising out the information we need to finish the installation.
		//Inconveniently for this script, asset import can cause a domain reload, meaning that the state of this script is discarded and
		//we need to resume installation later with some information we only have right now.
		//If no domain reload is needed, we can finalise installation 'seamlessly' from these callbacks
		manifestToInstall = manifest;
		AddCallbacks();
		AssetDatabase.ImportPackage(packageFilePath, true);
	}

	void AddCallbacks()
	{
		AssetDatabase.importPackageFailed += importPackageFailedCallback;
		AssetDatabase.importPackageCancelled += importPackageCancelledCallback;
		AssetDatabase.importPackageCompleted += importPackageCompletedCallback;
		AssetDatabase.importPackageStarted += importPackageStartedCallback;
	}

	void RemoveCallbacks()
	{
		AssetDatabase.importPackageFailed -= importPackageFailedCallback;
		AssetDatabase.importPackageCancelled -= importPackageCancelledCallback;
		AssetDatabase.importPackageCompleted -= importPackageCompletedCallback;
		AssetDatabase.importPackageStarted -= importPackageStartedCallback;
	}

	List<string> ExtractFileNames(string gzipFilename)
	{
		Dictionary<string, PackageEntry> packageContents = new Dictionary<string, PackageEntry>();
		using (FileStream compressedFileStream = File.OpenRead(gzipFilename))
		{
			GZipStream gzipFileStream = new GZipStream(compressedFileStream, CompressionMode.Decompress);
			BinaryReader reader = new BinaryReader(gzipFileStream);
			int readBufferSize = 1024 * 1024 * 10;
			int tarBlockSize = 512;
			byte[] readBuffer = new byte[readBufferSize];
			Regex hashPattern = new Regex(@"^([a-f\d]{20,})\/");

			while (true)
			{
				byte[] headerBuffer = reader.ReadBytes(tarBlockSize);                   //We want the header, but the header is padded to a blocksize
				if (headerBuffer.All(x => x == 0))
				{
					//Reached end of stream
					break;
				}
				GCHandle handle = GCHandle.Alloc(headerBuffer, GCHandleType.Pinned);
				TarHeader header;
				header = (TarHeader)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(TarHeader));
				handle.Free();

				string filename;
				unsafe
				{
					filename = Marshal.PtrToStringAnsi((IntPtr)header.filename, 100);
				}
				filename = filename.Trim();
				filename = filename.TrimEnd(new char[] { (char)0 });

				//Debug.Log((char)header.linkIndicator);
				string ustar;
				unsafe
				{
					ustar = Marshal.PtrToStringAnsi((IntPtr)header.ustar, 6);
				}
				string prefix = string.Empty;
				if (ustar.Equals("ustar"))
				{
					unsafe
					{
						prefix = Marshal.PtrToStringAnsi((IntPtr)header.prefix, 155);
					}
				}
				//Debug.Log(prefix + filename);
				prefix = prefix.Trim();
				prefix = prefix.TrimEnd(new char[] { (char)0 });

				string fullname = prefix + filename;
				Match hashMatch = hashPattern.Match(fullname);
				bool extractPathName = false;
				string hash = string.Empty;
				if (hashMatch.Success)
				{
					Group g = hashMatch.Groups[1];
					hash = g.Value;
					if (!packageContents.ContainsKey(hash))
					{
						packageContents[hash] = new PackageEntry();
					}
					PackageEntry entry = packageContents[hash];

					if (fullname.EndsWith("/asset.meta"))
					{
						entry.hasMeta = true;
					}
					if (fullname.EndsWith("/asset"))
					{
						entry.hasAsset = true;
					}
					if (fullname.EndsWith("/pathname"))
					{
						extractPathName = true;
					}

					packageContents[hash] = entry;
				}

				string rawFilesize;
				unsafe
				{
					rawFilesize = Marshal.PtrToStringAnsi((IntPtr)header.filesize, 12);
				}
				string filesize = rawFilesize.Trim();
				filesize = filesize.TrimEnd(new char[] { (char)0 });
				/*Debug.Log(filesize);
				foreach (byte fsChar in filesize)
				{
					Debug.Log(fsChar);
				}*/

				//Convert the octal string to a decimal number
				try
				{
					int filesizeInt = Convert.ToInt32(filesize, 8);
					int toRead = filesizeInt;
					int modulus = filesizeInt % tarBlockSize;
					if (modulus > 0)
						toRead += (tarBlockSize - modulus);    //Read the file and assume it's also 512 byte padded
					while (toRead > 0)
					{
						int readThisTime = Math.Min(readBufferSize, toRead);
						readBuffer = reader.ReadBytes(readThisTime);
						if (extractPathName)
						{
							if (toRead > readThisTime)
								throw new Exception("Assumed a pathname would fit in a single read!");
							string pathnameFileContents = Encoding.UTF8.GetString(readBuffer, 0, filesizeInt);
							PackageEntry entry = packageContents[hash];
							entry.pathname = FormatPath(pathnameFileContents.Split(new char[] { '\n' })[0]);
							packageContents[hash] = entry;
							//Debug.Log(entry.pathname);
						}
						toRead -= readThisTime;
					}
				}
				catch (Exception ex)
				{
					Debug.Log(String.Format("Caught Exception converting octal string to int: {0}", ex.Message));
					foreach (byte fsChar in filesize)
					{
						Debug.Log(fsChar);
					}
					throw;
				}
			}
		}
		List<string> filenames = new List<string>();
		Regex extensionRegex = new Regex(@"\/[^\/]*(\.\w*)", RegexOptions.IgnoreCase);
		foreach (PackageEntry entry in packageContents.Values)
		{
			if (entry.hasAsset)
				filenames.Add(entry.pathname);
			if (entry.hasMeta)
			{
				if (extensionRegex.IsMatch(entry.pathname))
					filenames.Add(entry.pathname.Substring(0, entry.pathname.LastIndexOf('.')) + ".meta");
				else
					filenames.Add(entry.pathname + ".meta");
			}
		}
		filenames.Sort();
		return filenames;
	}

	string ExtractPackageInfo(string gzipFilename)
	{
		using (FileStream gzipFileStream = File.OpenRead(gzipFilename))
		{
			BinaryReader reader = new BinaryReader(gzipFileStream);
			UInt16 count = Convert.ToUInt16(Marshal.SizeOf(typeof(GzipHeader)));
			byte[] readBuffer = new byte[count];
			readBuffer = reader.ReadBytes(count);
			GCHandle handle = GCHandle.Alloc(readBuffer, GCHandleType.Pinned);
			GzipHeader header;
			header = (GzipHeader)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(GzipHeader));
			handle.Free();

			//Basic sanity check here
			if (header.id1 != 31 || header.id2 != 139)
			{
				throw new Exception(string.Format("gzip header id byte(s) should be 31 and 139, but are {0} and {1} - invalid gzip header", header.id1, header.id2));
			}

			//Package must have extra data - this is where the package metadata lives as a JSON string
			if ((header.flags & 4) == 0)
			{
				Debug.LogWarningFormat("gzip header has no extra data so there is no package metadata supplied for {0}", gzipFilename);
				return string.Empty;
			}

			//Read the extra data size
			byte[] extraSizeBytes = new byte[2];
			extraSizeBytes = reader.ReadBytes(2);
			UInt16 extraSize = BitConverter.ToUInt16(extraSizeBytes, 0);    //This should handle endianness. Touch wood...

			count = Convert.ToUInt16(Marshal.SizeOf(typeof(GzipHeaderExtraField)));
			while (extraSize > count)
			{
				//Read extra data fields
				readBuffer = new byte[count];
				readBuffer = reader.ReadBytes(count);
				//This contains a field we need to endian swap - so is created differently
				GzipHeaderExtraField extra = new GzipHeaderExtraField(readBuffer[0], readBuffer[1], BitConverter.ToUInt16(readBuffer, 2));

				extraSize -= count;

				if (extra.id1 == (byte)'A')
				{
					if (extra.id2 == (byte)'$')
					{
						//We have our JSON string with the metadata, read it in and return it
						return Encoding.Default.GetString(reader.ReadBytes(extra.length));
					}
				}
				gzipFileStream.Seek(extra.length, SeekOrigin.Current);

				extraSize -= extra.length;
			}
		}

		throw new Exception("Didn't find a package info string - is this a valid package?");
	}
};
