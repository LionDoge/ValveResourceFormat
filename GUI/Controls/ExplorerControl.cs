using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Utils;
using ValveKeyValue;

namespace GUI.Controls
{
    partial class ExplorerControl : UserControl
    {
        private readonly List<(TreeNode ParentNode, int AppID, TreeNode[] Children)> TreeData = new();

        public ExplorerControl()
        {
            InitializeComponent();

#if DEBUG
            var timer = Stopwatch.StartNew();
#endif

            try
            {
                treeView.BeginUpdate();
                treeView.ImageList = MainForm.ImageList;
                Scan();
            }
            finally
            {
                treeView.EndUpdate();
            }

#if DEBUG
            timer.Stop();
            Console.WriteLine($"Explorer scan time: {timer.Elapsed}");
#endif
        }

        private void Scan()
        {
            var vpkImage = MainForm.ImageList.Images.IndexOfKey("vpk");
            var vcsImage = MainForm.ImageList.Images.IndexOfKey("vcs");
            var mapImage = MainForm.ImageList.Images.IndexOfKey("map");
            var folderImage = MainForm.ImageList.Images.IndexOfKey("_folder");
            var pluginImage = MainForm.ImageList.Images.IndexOfKey("_plugin");

            var steam = Settings.GetSteamPath();

            var kvDeserializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);

            var libraryfolders = Path.Join(steam, "libraryfolders.vdf");
            KVObject libraryFoldersKv;

            using (var libraryFoldersStream = File.OpenRead(libraryfolders))
            {
                libraryFoldersKv = kvDeserializer.Deserialize(libraryFoldersStream, KVSerializerOptions.DefaultOptions);
            }

            var steamPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };

            foreach (var child in libraryFoldersKv.Children)
            {
                steamPaths.Add(Path.GetFullPath(Path.Join(child["path"].ToString(), "steamapps")));
            }

            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 5,
                BufferSize = 65536,
            };

            Parallel.ForEach(steamPaths, (steamPath) =>
            {
                var manifests = Directory.GetFiles(steamPath, "appmanifest_*.acf");

                foreach (var appManifestPath in manifests)
                {
                    KVObject appManifestKv;

                    try
                    {
                        using var appManifestStream = File.OpenRead(appManifestPath);
                        appManifestKv = kvDeserializer.Deserialize(appManifestStream, KVSerializerOptions.DefaultOptions);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    var appId = appManifestKv["appid"].ToInt32(CultureInfo.InvariantCulture);
                    var appName = appManifestKv["name"].ToString();
                    var installDir = appManifestKv["installdir"].ToString();
                    var gamePath = Path.Combine(steamPath, "common", installDir);

                    if (!Directory.Exists(gamePath))
                    {
                        continue;
                    }

                    if (appId is 1237970 or 1454890 or 1172470)
                    {
                        // Ignore Apex Legends, Titanfall, Titanfall 2 because Respawn has customized VPK format and VRF can't open it
                        continue;
                    }

                    var foundFiles = new List<TreeNode>();

                    // Find all the vpks in game folder
                    var vpks = new FileSystemEnumerable<string>(
                        gamePath,
                        (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath(),
                        enumerationOptions)
                    {
                        ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory && Path.GetExtension(entry.FileName).Equals(".vpk", StringComparison.Ordinal)
                    };

                    foreach (var vpk in vpks)
                    {
                        if (Regexes.VpkNumberArchive.IsMatch(vpk))
                        {
                            continue;
                        }

                        var image = vpkImage;
                        var vpkName = vpk[(gamePath.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');

                        if (Path.GetFileName(vpkName).StartsWith("shaders_", StringComparison.Ordinal))
                        {
                            image = vcsImage;
                        }
                        else if (vpkName.Contains("/maps/", StringComparison.Ordinal))
                        {
                            image = mapImage;
                        }

                        foundFiles.Add(new TreeNode(vpkName)
                        {
                            Tag = vpk,
                            ImageIndex = image,
                            SelectedImageIndex = image,
                        });
                    }

                    if (foundFiles.Count == 0)
                    {
                        continue;
                    }

                    // Find workshop content
                    try
                    {

                        KVObject workshopInfo;
                        var workshopManifest = Path.Join(steamPath, "workshop", $"appworkshop_{appId}.acf");

                        if (File.Exists(workshopManifest))
                        {
                            using (var stream = File.OpenRead(workshopManifest))
                            {
                                workshopInfo = kvDeserializer.Deserialize(stream);
                            }

                            foreach (var item in (IEnumerable<KVObject>)workshopInfo["WorkshopItemsInstalled"])
                            {
                                var addonPath = Path.Join(steamPath, "workshop", "content", appId.ToString(), item.Name);
                                var publishDataPath = Path.Join(addonPath, "publish_data.txt");
                                var vpk = Path.Join(addonPath, $"{item.Name}.vpk");

                                if (!File.Exists(vpk))
                                {
                                    continue;
                                }

                                using var stream = File.OpenRead(publishDataPath);
                                var publishData = kvDeserializer.Deserialize(stream);
                                var addonTitle = publishData["title"];

                                foundFiles.Add(new TreeNode($"[Workshop {item.Name}] {addonTitle}")
                                {
                                    Tag = vpk,
                                    ImageIndex = pluginImage,
                                    SelectedImageIndex = pluginImage,
                                });
                            }
                        }
                    }
                    catch (Exception)
                    {
                        //
                    }

                    // Sort the files and create the nodes
                    foundFiles.Sort((a, b) => string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase));
                    var foundFilesArray = foundFiles.ToArray();

                    var treeNodeName = $"[{appId}] {appName} - {gamePath.Replace(Path.DirectorySeparatorChar, '/')}";
                    var treeNode = new TreeNode(treeNodeName)
                    {
                        Tag = gamePath,
                        ImageIndex = folderImage,
                        SelectedImageIndex = folderImage,
                    };
                    treeNode.Nodes.AddRange(foundFilesArray);
                    treeNode.Expand();

                    lock (TreeData)
                    {
                        TreeData.Add((treeNode, appId, foundFilesArray));
                    }
                }
            });

            // Recent files
            {
                var recentFiles = GetRecentFileNodes();
                var recentImage = MainForm.ImageList.Images.IndexOfKey("_recent");
                var recentFilesTreeNode = new TreeNode("Recent files")
                {
                    ImageIndex = recentImage,
                    SelectedImageIndex = recentImage,
                    ContextMenuStrip = recentFilesContextMenuStrip,
                };
                recentFilesTreeNode.Nodes.AddRange(recentFiles);
                recentFilesTreeNode.Expand();

                TreeData.Add((recentFilesTreeNode, -1, recentFiles));
            }

            TreeData.Sort((a, b) => a.AppID - b.AppID);

            treeView.Nodes.AddRange(TreeData.Select(node => node.ParentNode).ToArray());
        }

        private void OnTreeViewNodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            var path = (string)e.Node.Tag;

            if (File.Exists(path))
            {
                Program.MainForm.OpenFile(path);
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo()
                {
                    FileName = path + Path.DirectorySeparatorChar,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
        }
        private void OnTreeViewNodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node.Tag != null && e.Button == MouseButtons.Right)
            {
                e.Node.TreeView.SelectedNode = e.Node;

                fileContextMenuStrip.Show(e.Node.TreeView, e.Location);
            }
        }

        private void OnFilterTextBoxTextChanged(object sender, EventArgs e)
        {
            treeView.BeginUpdate();
            treeView.Nodes.Clear();

            var foundNodes = new List<TreeNode>(TreeData.Count);

            foreach (var node in TreeData)
            {
                node.ParentNode.Nodes.Clear();

                var foundChildren = Array.FindAll(node.Children, (child) =>
                {
                    return child.Text.Contains(filterTextBox.Text, StringComparison.OrdinalIgnoreCase);
                });

                if (foundChildren.Any())
                {
                    node.ParentNode.Nodes.AddRange(foundChildren);
                    foundNodes.Add(node.ParentNode);
                }
            }

            treeView.Nodes.AddRange(foundNodes.ToArray());
            treeView.EndUpdate();
        }

        private void OnVisibleChanged(object sender, EventArgs e)
        {
            // Refresh recent files list whenever explorer becomes visible
            if (!Visible)
            {
                return;
            }

            treeView.BeginUpdate();
            var recentFiles = GetRecentFileNodes();
            var recentFilesNode = TreeData.Find(node => node.AppID == -1);
            recentFilesNode.ParentNode.Nodes.Clear();
            recentFilesNode.ParentNode.Nodes.AddRange(recentFiles);
            recentFilesNode.Children = recentFiles;
            treeView.EndUpdate();
        }

        private static TreeNode[] GetRecentFileNodes()
        {
            return Settings.Config.RecentFiles.Select(path =>
            {
                var pathDisplay = path.Replace(Path.DirectorySeparatorChar, '/');
                var extension = Path.GetExtension(path);

                if (extension == ".vpk" && pathDisplay.Contains("/maps/", StringComparison.Ordinal))
                {
                    extension = ".map";
                }

                var imageIndex = MainForm.GetImageIndexForExtension(extension);

                var toAdd = new TreeNode(pathDisplay)
                {
                    Tag = path,
                    ImageIndex = imageIndex,
                    SelectedImageIndex = imageIndex,
                };

                return toAdd;
            }).Reverse().ToArray();
        }

        private void OnClearRecentFilesClick(object sender, EventArgs e)
        {
            Settings.ClearRecentFiles();

            var recentFilesNode = TreeData.Find(node => node.AppID == -1);
            recentFilesNode.ParentNode.Nodes.Clear();
            recentFilesNode.Children = Array.Empty<TreeNode>();
        }

        private void OnRevealInFileExplorerClick(object sender, EventArgs e)
        {
            var control = (TreeView)((ContextMenuStrip)((ToolStripMenuItem)sender).Owner).SourceControl;

            if (control.SelectedNode.Tag == null)
            {
                return;
            }

            var path = (string)control.SelectedNode.Tag;

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo()
                {
                    FileName = "explorer.exe",
                    Arguments = @$"/select, ""{path}"""
                });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo()
                {
                    FileName = path + Path.DirectorySeparatorChar,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
        }
    }
}
