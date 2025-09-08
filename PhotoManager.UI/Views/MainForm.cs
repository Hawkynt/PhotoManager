using PhotoManager.Core.Enums;
using PhotoManager.UI.Controllers;
using PhotoManager.UI.Models;

namespace PhotoManager.UI.Views;

public partial class MainForm : Form {
  private readonly MainController _controller;
  private readonly MainViewModel _viewModel;

  public MainForm(MainController controller, MainViewModel viewModel) {
    this._controller = controller;
    this._viewModel = viewModel;
    this.InitializeComponent();
    this.InitializeControls();
    this.BindViewModel();
    
    // Load settings after form is shown to avoid UI thread deadlock
    this.Shown += async (s, e) => await this._controller.LoadSettingsAsync();
  }

  private void InitializeControls() {
    // Initialize ComboBox with DuplicateHandling enum values
    cmbDuplicateHandling.Items.AddRange(Enum.GetValues<DuplicateHandling>()
      .Cast<object>().ToArray());
    cmbDuplicateHandling.SelectedItem = _viewModel.DuplicateHandling;
    
    // Set initial checkbox states
    chkRecursive.Checked = _viewModel.Recursive;
    chkPreserveOriginals.Checked = _viewModel.PreserveOriginals;
    
    // Wire up events
    cmbDuplicateHandling.SelectedIndexChanged += (s, e) => {
      if (cmbDuplicateHandling.SelectedItem is DuplicateHandling selected) {
        _viewModel.DuplicateHandling = selected;
      }
    };
    
    chkRecursive.CheckedChanged += (s, e) => {
      _viewModel.Recursive = chkRecursive.Checked;
    };
    
    chkPreserveOriginals.CheckedChanged += (s, e) => {
      _viewModel.PreserveOriginals = chkPreserveOriginals.Checked;
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
      case nameof(MainViewModel.SourceDirectory):
        this.txtSourceDirectory.Text = this._viewModel.SourceDirectory;
        break;
      case nameof(MainViewModel.DestinationDirectory):
        this.txtDestinationDirectory.Text = this._viewModel.DestinationDirectory;
        break;
      case nameof(MainViewModel.IsProcessing):
        this.btnProcess.Enabled = !this._viewModel.IsProcessing;
        this.btnCancel.Enabled = this._viewModel.IsProcessing;
        this.btnSelectSource.Enabled = !this._viewModel.IsProcessing;
        this.btnSelectDestination.Enabled = !this._viewModel.IsProcessing;
        this.cmbDuplicateHandling.Enabled = !this._viewModel.IsProcessing;
        this.chkRecursive.Enabled = !this._viewModel.IsProcessing;
        this.chkPreserveOriginals.Enabled = !this._viewModel.IsProcessing;
        break;
      case nameof(MainViewModel.ProgressValue):
        this.progressBar.Value = this._viewModel.ProgressValue;
        break;
      case nameof(MainViewModel.StatusMessage):
        this.lblStatus.Text = this._viewModel.StatusMessage;
        break;
      case nameof(MainViewModel.DuplicateHandling):
        if (this.cmbDuplicateHandling.SelectedItem as DuplicateHandling? != this._viewModel.DuplicateHandling) {
          this.cmbDuplicateHandling.SelectedItem = this._viewModel.DuplicateHandling;
        }
        break;
      case nameof(MainViewModel.Recursive):
        this.chkRecursive.Checked = this._viewModel.Recursive;
        break;
      case nameof(MainViewModel.PreserveOriginals):
        this.chkPreserveOriginals.Checked = this._viewModel.PreserveOriginals;
        break;
    }
  }

  private async void btnProcess_Click(object sender, EventArgs e) {
    await this._controller.ProcessDirectoryAsync();
  }

  private void btnCancel_Click(object sender, EventArgs e) {
    this._controller.CancelProcessing();
  }

  private void btnSelectSource_Click(object sender, EventArgs e) {
    this._controller.SelectSourceDirectory();
  }

  private void btnSelectDestination_Click(object sender, EventArgs e) {
    this._controller.SelectDestinationDirectory();
  }
}
