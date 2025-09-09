namespace PhotoManager.UI.Views;

partial class AboutForm {
  private System.ComponentModel.IContainer components = null;
  private Label lblTitle;
  private Label lblVersion;
  private Label lblCopyright;
  private Label lblDescription;
  private Button btnOk;
  private PictureBox pictureBoxIcon;

  protected override void Dispose(bool disposing) {
    if (disposing && (components != null)) {
      components.Dispose();
    }
    base.Dispose(disposing);
  }

  private void InitializeComponent() {
    lblTitle = new Label();
    lblVersion = new Label();
    lblCopyright = new Label();
    lblDescription = new Label();
    btnOk = new Button();
    pictureBoxIcon = new PictureBox();
    
    ((System.ComponentModel.ISupportInitialize)pictureBoxIcon).BeginInit();
    SuspendLayout();

    // pictureBoxIcon
    pictureBoxIcon.Location = new Point(12, 12);
    pictureBoxIcon.Name = "pictureBoxIcon";
    pictureBoxIcon.Size = new Size(48, 48);
    pictureBoxIcon.SizeMode = PictureBoxSizeMode.StretchImage;
    pictureBoxIcon.TabIndex = 0;
    pictureBoxIcon.TabStop = false;
    pictureBoxIcon.Image = SystemIcons.Application.ToBitmap();

    // lblTitle
    lblTitle.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Bold);
    lblTitle.Location = new Point(78, 12);
    lblTitle.Name = "lblTitle";
    lblTitle.Size = new Size(294, 23);
    lblTitle.TabIndex = 1;
    lblTitle.Text = "Photo Manager";

    // lblVersion
    lblVersion.Location = new Point(78, 44);
    lblVersion.Name = "lblVersion";
    lblVersion.Size = new Size(294, 16);
    lblVersion.TabIndex = 2;
    lblVersion.Text = "Version 1.0.0";

    // lblCopyright
    lblCopyright.Location = new Point(12, 80);
    lblCopyright.Name = "lblCopyright";
    lblCopyright.Size = new Size(360, 16);
    lblCopyright.TabIndex = 3;
    lblCopyright.Text = "Copyright © 2025";

    // lblDescription
    lblDescription.Location = new Point(12, 108);
    lblDescription.Name = "lblDescription";
    lblDescription.Size = new Size(360, 60);
    lblDescription.TabIndex = 4;
    lblDescription.Text = "Windows Forms application for organizing and managing photo collections.";

    // btnOk
    btnOk.DialogResult = DialogResult.OK;
    btnOk.Location = new Point(297, 186);
    btnOk.Name = "btnOk";
    btnOk.Size = new Size(75, 23);
    btnOk.TabIndex = 5;
    btnOk.Text = "OK";
    btnOk.UseVisualStyleBackColor = true;
    btnOk.Click += BtnOk_Click;

    // AboutForm
    AcceptButton = btnOk;
    AutoScaleDimensions = new SizeF(7F, 15F);
    AutoScaleMode = AutoScaleMode.Font;
    ClientSize = new Size(384, 221);
    Controls.Add(btnOk);
    Controls.Add(lblDescription);
    Controls.Add(lblCopyright);
    Controls.Add(lblVersion);
    Controls.Add(lblTitle);
    Controls.Add(pictureBoxIcon);
    FormBorderStyle = FormBorderStyle.FixedDialog;
    MaximizeBox = false;
    MinimizeBox = false;
    Name = "AboutForm";
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.CenterParent;
    Text = "About";
    
    ((System.ComponentModel.ISupportInitialize)pictureBoxIcon).EndInit();
    ResumeLayout(false);
  }
}