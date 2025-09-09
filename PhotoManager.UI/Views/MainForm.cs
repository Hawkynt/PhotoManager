using PhotoManager.Core.Enums;
using PhotoManager.Core.Models;
using PhotoManager.Core.Services;
using PhotoManager.UI.Controllers;
using PhotoManager.UI.Models;
using SixLabors.ImageSharp;
using System.ComponentModel;

namespace PhotoManager.UI.Views;

public partial class MainForm : Form {


  private const string? _UI_DATE_FORMAT = "dd.MM.yyyy HH:mm:ss";
  private readonly MainController _controller;
  private readonly MainViewModel _viewModel;

  // TODO: the view should not directly interact with the model
  public MainForm(MainController controller, MainViewModel viewModel) {
    this._controller = controller;
    this._viewModel = viewModel;
    this.InitializeComponent();
    this.InitializeFormControls();
    this.BindViewModel();
    
    // Load settings after form is shown to avoid UI thread deadlock
    this.Shown += async (s, e) => {
      await this._controller.LoadSettingsAsync();
      this.LoadTreeViewState();
      this.SetupSplitterDistances();
    };
  }

  private void InitializeFormControls() {
    // Enable double buffering for better DataGridView performance
    this.dataGridViewFiles.EnableDoubleBuffering();
    
    // Initialize ComboBox with DuplicateHandling enum values
    this.cmbDuplicateHandling.Items.AddRange(Enum.GetValues<DuplicateHandling>()
      .Cast<object>().ToArray());
    this.cmbDuplicateHandling.SelectedItem = this._viewModel.DuplicateHandling;
    
    // Set initial checkbox states
    this.chkPreserveOriginals.Checked = this._viewModel.PreserveOriginals;
    
    // Wire up events
    this.cmbDuplicateHandling.SelectedIndexChanged += (s, e) => {
      if (this.cmbDuplicateHandling.SelectedItem is DuplicateHandling selected) {
        this._viewModel.DuplicateHandling = selected;
      }
    };

    this.chkPreserveOriginals.CheckedChanged += (s, e) => {
      this._viewModel.PreserveOriginals = this.chkPreserveOriginals.Checked;
    };
  }

  private void BindViewModel() {
    // Bind controls to view model properties
    this._viewModel.PropertyChanged += (s, e) => {
      if (this.InvokeRequired)
        this.Invoke(() => this.UpdateUI(e.PropertyName));
      else
        this.UpdateUI(e.PropertyName);
    };
  }

  private void UpdateUI(string? propertyName) {
    switch (propertyName) {
      case nameof(MainViewModel.IsProcessing):
        this.btnRun.Enabled = !this._viewModel.IsProcessing;
        this.btnCancel.Enabled = this._viewModel.IsProcessing;
        this.btnScan.Enabled = !this._viewModel.IsProcessing;
        this.cmbDuplicateHandling.Enabled = !this._viewModel.IsProcessing;
        this.chkPreserveOriginals.Enabled = !this._viewModel.IsProcessing;
        this.treeViewSources.Enabled = !this._viewModel.IsProcessing;
        break;
      case nameof(MainViewModel.ProgressValue):
        this.toolStripProgressBar.Value = this._viewModel.ProgressValue;
        break;
      case nameof(MainViewModel.StatusMessage):
        this.toolStripStatusLabel.Text = this._viewModel.StatusMessage;
        break;
      case nameof(MainViewModel.DuplicateHandling):
        if (this.cmbDuplicateHandling.SelectedItem as DuplicateHandling? != this._viewModel.DuplicateHandling) {
          this.cmbDuplicateHandling.SelectedItem = this._viewModel.DuplicateHandling;
        }
        break;
      case nameof(MainViewModel.PreserveOriginals):
        this.chkPreserveOriginals.Checked = this._viewModel.PreserveOriginals;
        break;
      case nameof(MainViewModel.DestinationDirectory):
        this.txtOutputPath.Text = this._viewModel.DestinationDirectory ?? string.Empty;
        break;
    }
  }

  // New event handlers for the redesigned UI
  private void BtnScan_Click(object sender, EventArgs e) {
    this.ScanSourceFiles();
  }

  private async void ScanSourceFiles() {
    // Clear existing files and reset data source
    this.dataGridViewFiles.DataSource = null;
    
    // Get all checked source paths from TreeView (UI thread operation)
    var sourcePaths = this._controller.GetCheckedSourcePaths(this.treeViewSources.Nodes.Cast<TreeNode>());
    
    this.btnScan.Enabled = false;
    this.btnRun.Enabled = false;
    this.toolStripStatusLabel.Text = "Scanning files...";

    try {
      // Force UI update before starting background work
      Application.DoEvents();
      
      // Run the heavy scanning work on background thread
      var fileItems = await Task.Run(() => this._controller.ScanSourceFilesAsync(sourcePaths));
      
      // Update UI on UI thread
      this.dataGridViewFiles.DataSource = fileItems;
      this.btnRun.Enabled = fileItems.Count > 0;
      this.toolStripStatusLabel.Text = $"Found {fileItems.Count} files";
      
    } catch (Exception ex) {
      this.toolStripStatusLabel.Text = $"Scan error: {ex.Message}";
    } finally {
      this.btnScan.Enabled = true;
    }
  }




  private void BtnRun_Click(object __, EventArgs ___) {
    // Get files from DataGridView
    if (this.dataGridViewFiles.DataSource is SortableBindingList<FileItemModel> fileItems) {
      _ = this._controller.ProcessSelectedFilesAsync(fileItems);
    }
  }
  
  private void BtnCancel_Click(object __, EventArgs ___) => this._controller.CancelProcessing();

  private void AddPathToolStripMenuItem_Click(object __, EventArgs ___) {
    using var dialog = new FolderBrowserDialog {
      Description = Resources.Strings.Dialog_SelectFolder,
      ShowNewFolderButton = false
    };

    if (dialog.ShowDialog() != DialogResult.OK)
      return;

    this.AddSourcePath(new(dialog.SelectedPath), true);
    this.SaveTreeViewState();
    _ = this._controller.SaveSettingsAsync(); // Fire and forget
  }

  // TODO: isnt this controller's job?
  private void AddSourcePath(DirectoryInfo fullPath, bool recursive) {
    var displayText = _CreateDisplayName(fullPath) + (recursive ? Resources.Strings.TreeView_Recursive : Resources.Strings.TreeView_NonRecursive);
    var node = new TreeNode(displayText) {
      Tag = new TreeViewNodeData(fullPath, recursive),
      Checked = true,
      ToolTipText = fullPath.FullName
    };

    this.treeViewSources.Nodes.Add(node);
    
    if (recursive) {
      this.LoadSubDirectories(node, fullPath);
    }
  }

  // TODO: isnt this controller's job?
  private static string _CreateDisplayName(DirectoryInfo fullPath) {
    var displayName = Path.GetFileName(fullPath.FullName);
    if (string.IsNullOrEmpty(displayName))
      displayName = fullPath.FullName; // Drive root like "C:\"
    return displayName;
  }

  // TODO: isnt this controller's job?
  private void LoadSubDirectories(TreeNode parentNode, DirectoryInfo parentDir) {
    try {
      var subDirectories = parentDir.GetDirectories();
      foreach (var directory in subDirectories) {
        var childNode = new TreeNode(directory.Name) {
          Tag = new TreeViewNodeData(directory, false),
          Checked = true,
          ToolTipText = directory.FullName
        };
        parentNode.Nodes.Add(childNode);
      }
    }
    catch {
      // Ignore access denied or other exceptions
    }
  }

  private void RemovePathToolStripMenuItem_Click(object __, EventArgs ___) {
    if (this.treeViewSources.SelectedNode == null)
      return;

    this.treeViewSources.Nodes.Remove(this.treeViewSources.SelectedNode);
    this.SaveTreeViewState();
    _ = this._controller.SaveSettingsAsync(); // Fire and forget
  }

  private void ToggleRecursiveToolStripMenuItem_Click(object __, EventArgs ___) {
    if (this.treeViewSources.SelectedNode?.Tag is not TreeViewNodeData data)
      return;

    var newRecursive = !data.Recursive;
    var newTag = new TreeViewNodeData(data.Path, newRecursive);
      
    // Update the node
    this.treeViewSources.SelectedNode.Tag = newTag;
      
    // Update display text
    var recursiveText = newRecursive ? Resources.Strings.TreeView_Recursive : Resources.Strings.TreeView_NonRecursive;
    this.treeViewSources.SelectedNode.Text = _CreateDisplayName(data.Path) + recursiveText;

    switch (newRecursive) {
      case true when this.treeViewSources.SelectedNode.Nodes.Count == 0:
        // Update child nodes
        this.LoadSubDirectories(this.treeViewSources.SelectedNode, data.Path);
        break;
      case false:
        this.treeViewSources.SelectedNode.Nodes.Clear();
        break;
    }

    this.SaveTreeViewState();
    _ = this._controller.SaveSettingsAsync(); // Fire and forget
  }

  private void TreeViewSources_AfterCheck(object __, TreeViewEventArgs ___) {
    // Save TreeView state when checkboxes change
    this.SaveTreeViewState();
    _ = this._controller.SaveSettingsAsync(); // Fire and forget
  }

  private void DataGridViewFiles_SelectionChanged(object __, EventArgs ___) {
    if (this.dataGridViewFiles.SelectedRows.Count <= 0)
      return;

    var row = this.dataGridViewFiles.SelectedRows[0];
    if (row.DataBoundItem is not FileItemModel { FileInfo.Exists: true } fileItem)
      return;

    this.LoadImagePreview(fileItem.FileInfo);
    _ = this.LoadFileMetadata(fileItem.FileInfo); // Fire and forget
  }

  // TODO: isnt this controller's job?
  private void LoadImagePreview(FileInfo file) {
    try {
      // First try with System.Drawing.Image for standard formats
      try {
        using var stream = file.Open(FileMode.Open,FileAccess.Read,FileShare.Read);
        this.pictureBoxPreview.Image = System.Drawing.Image.FromStream(stream);
        return;
      }
      catch {
        // If System.Drawing fails, try with ImageSharp for other formats
      }

      // Try with ImageSharp for formats not supported by System.Drawing
      using var imageSharpStream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
      using var imageSharpImage = SixLabors.ImageSharp.Image.Load(imageSharpStream);
      
      // Convert ImageSharp image to System.Drawing.Bitmap for PictureBox
      using var memoryStream = new MemoryStream();
      imageSharpImage.SaveAsBmp(memoryStream);
      memoryStream.Position = 0;
      this.pictureBoxPreview.Image = System.Drawing.Image.FromStream(memoryStream);
    }
    catch {
      // If both approaches fail, show placeholder or clear the preview
      this.pictureBoxPreview.Image = null;
    }
  }

  // TODO: isnt this controller's job?
  private async Task LoadFileMetadata(FileInfo file) {
    
    // Get EXIF, GPS, and filename dates from Core services
    var fileToImport = new FileToImport(file);
    
    DateTime? exifOriginalDate = null;
    DateTime? exifModifiedDate = null;
    DateTime? gpsDate = null;
    DateTime? filenameDate = null;
    
    try {
      await foreach (var date in fileToImport.GetExifSubIfdDateAsync()) {
        exifOriginalDate = date;
        break; // Take the first one
      }
      
      await foreach (var date in fileToImport.GetExifIfd0DateAsync()) {
        exifModifiedDate = date;
        break; // Take the first one
      }
      
      await foreach (var date in fileToImport.GetGpsDateAsync()) {
        gpsDate = date;
        break; // Take the first one
      }
      
      // Use Core DateTimeParser service for filename date extraction
      var dateTimeParser = new DateTimeParser();
      var importSettings = new ImportSettings();
      await foreach (var date in dateTimeParser.ParseDateFromFileName(fileToImport, importSettings)) {
        filenameDate = date;
        break; // Take the first one
      }
    } catch {
      // Ignore metadata extraction errors
    }

    // Get the winning date source from the ImportManager (WYSIWYG)
    var (_, winningSource) = await this._controller.GetMostLogicalDateWithSourceAsync(fileToImport);

    var metadataModel = new FileMetadataModel {
      FileName = file.Name,
      FilePath = file.FullName,
      FileSize = this.FormatFileSize(file.Length),
      DateCreated = file.CreationTime.ToString(_UI_DATE_FORMAT),
      DateModified = file.LastWriteTime.ToString(_UI_DATE_FORMAT),
      FilenameDate = filenameDate?.ToString(_UI_DATE_FORMAT) ?? Resources.Strings.Metadata_NotDetected,
      ExifOriginalDate = exifOriginalDate?.ToString(_UI_DATE_FORMAT) ?? Resources.Strings.Metadata_NotFound,
      ExifModifiedDate = exifModifiedDate?.ToString(_UI_DATE_FORMAT) ?? Resources.Strings.Metadata_NotFound,
      GpsDate = gpsDate?.ToString(_UI_DATE_FORMAT) ?? Resources.Strings.Metadata_NotFound
    };

    // Use the custom tinted metadata view with the winning source highlighted
    this.tintedMetadataView.SetMetadata(metadataModel, winningSource);
  }

  // TODO: isnt this model's job?
  private string FormatFileSize(long bytes) {
    string[] sizes = [Resources.Strings.FileSize_Bytes, Resources.Strings.FileSize_Kilobytes, Resources.Strings.FileSize_Megabytes, Resources.Strings.FileSize_Gigabytes];
    double len = bytes;
    var order = 0;
    while (len >= 1024 && order < sizes.Length - 1) {
      ++order;
      len /= 1024;
    }
    return $"{len:0.##} {sizes[order]}";
  }

  private void BtnSelectOutput_Click(object __, EventArgs ___) {
    this._controller.SelectDestinationDirectory();
  }

  private void PictureBoxPreview_DoubleClick(object __, EventArgs ___) {
    if (this.dataGridViewFiles.SelectedRows.Count <= 0) 
      return;

    var row = this.dataGridViewFiles.SelectedRows[0];
    if (row.DataBoundItem is not FileItemModel { FileInfo.Exists: true } fileItem) 
      return;

    var filePath = fileItem.FileInfo;
    try {
      // Use Process.Start to open the image with the system's default image viewer
      var processInfo = new System.Diagnostics.ProcessStartInfo {
        FileName = filePath.FullName,
        UseShellExecute = true
      };
      System.Diagnostics.Process.Start(processInfo);
    } catch (Exception ex) {
      MessageBox.Show(string.Format(Resources.Strings.Error_CouldNotOpenImage, ex.Message), Resources.Strings.Error_Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
  }

  private void AboutToolStripMenuItem_Click(object __, EventArgs ___) {
    // TODO: isnt this controller's job?
    var aboutController = new AboutController();
    using var aboutForm = new AboutForm(aboutController);
    aboutForm.ShowDialog(this);
  }

  private void SetupSplitterDistances() {
    // Set minimum sizes and splitter distances after form is properly sized
    
    // Main splitter (TreeView | DataGridView+Preview)
    this.mainSplitContainer.Panel1MinSize = 150;
    this.mainSplitContainer.Panel2MinSize = 200;
    if (this.mainSplitContainer.Width > 350) {
      this.mainSplitContainer.SplitterDistance = Math.Min(250, this.mainSplitContainer.Width - 200);
    }
    
    // Right splitter (DataGridView | Preview+Metadata)
    this.rightSplitContainer.Panel1MinSize = 100;
    this.rightSplitContainer.Panel2MinSize = 100;
    if (this.rightSplitContainer.Width > 200) {
      this.rightSplitContainer.SplitterDistance = Math.Min(this.rightSplitContainer.Width - 300, this.rightSplitContainer.Width * 2 / 3);
    }
    
    // Preview/Metadata splitter (Preview | Metadata)
    this.previewMetadataSplitContainer.Panel1MinSize = 50;
    this.previewMetadataSplitContainer.Panel2MinSize = 50;
    if (this.previewMetadataSplitContainer.Height > 100) {
      this.previewMetadataSplitContainer.SplitterDistance = Math.Min(200, this.previewMetadataSplitContainer.Height / 2);
    }
  }

  // TODO: isnt this controller's job?
  private void LoadTreeViewState() {
    this.treeViewSources.Nodes.Clear();
    
    foreach (var pathData in this._viewModel.TreeViewPaths) {
      var directory = new DirectoryInfo(pathData.Path);
      if (!directory.Exists)
        continue;

      this.AddSourcePath(new(pathData.Path), pathData.Recursive);
        
      // Set the checkbox state
      var node = this.treeViewSources.Nodes.Cast<TreeNode>()
        .FirstOrDefault(n => n.Tag is TreeViewNodeData nodeData && nodeData.Path.FullName == pathData.Path);
      
      if (node != null)
        node.Checked = pathData.Checked;
    }
  }

  // TODO: isnt this controller's job?
  private void SaveTreeViewState() {
    var pathDataList = new List<TreeViewPathData>();
    
    foreach (TreeNode node in this.treeViewSources.Nodes) {
      if (node.Tag is not TreeViewNodeData nodeData)
        continue;

      pathDataList.Add(new TreeViewPathData {
        Path = nodeData.Path.FullName,
        Recursive = nodeData.Recursive,
        Checked = node.Checked
      });
    }

    this._viewModel.TreeViewPaths = pathDataList;
  }
}
