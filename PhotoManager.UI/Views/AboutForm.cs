using PhotoManager.UI.Controllers;
using PhotoManager.UI.Models;

namespace PhotoManager.UI.Views;

public partial class AboutForm : Form {
  private readonly AboutController _controller;
  private readonly AboutViewModel _viewModel;

  public AboutForm(AboutController controller) {
    this._controller = controller;
    this._viewModel = controller.GetAboutInfo();

    this.InitializeComponent();
    this.LoadData();
  }

  private void LoadData() {
    this.Text = string.Format(Resources.Strings.About_Title, this._viewModel.Title);
    this.lblTitle.Text = this._viewModel.Title;
    this.lblVersion.Text = string.Format(Resources.Strings.About_Version, this._viewModel.Version);
    this.lblCopyright.Text = string.Format(Resources.Strings.About_Copyright, this._viewModel.Copyright);
    this.lblDescription.Text = string.Format(Resources.Strings.About_Description, this._viewModel.Description);
  }

  private void BtnOk_Click(object sender, EventArgs e) {
    this.Close();
  }
}