namespace PhotoManager.UI.Views;

partial class MainForm {
    private System.ComponentModel.IContainer components = null;
    private TextBox txtSourceDirectory;
    private TextBox txtDestinationDirectory;
    private Button btnSelectSource;
    private Button btnSelectDestination;
    private Button btnProcess;
    private Button btnCancel;
    private ProgressBar progressBar;
    private Label lblStatus;
    private Label lblSource;
    private Label lblDestination;
    private ComboBox cmbDuplicateHandling;
    private Label lblDuplicateHandling;
    private CheckBox chkRecursive;
    private CheckBox chkPreserveOriginals;

    protected override void Dispose(bool disposing) {
        if (disposing && (components != null)) {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent() {
        txtSourceDirectory = new TextBox();
        txtDestinationDirectory = new TextBox();
        btnSelectSource = new Button();
        btnSelectDestination = new Button();
        btnProcess = new Button();
        btnCancel = new Button();
        progressBar = new ProgressBar();
        lblStatus = new Label();
        lblSource = new Label();
        lblDestination = new Label();
        cmbDuplicateHandling = new ComboBox();
        lblDuplicateHandling = new Label();
        chkRecursive = new CheckBox();
        chkPreserveOriginals = new CheckBox();
        SuspendLayout();
        
        // lblSource
        lblSource.AutoSize = true;
        lblSource.Location = new Point(12, 15);
        lblSource.Name = "lblSource";
        lblSource.Size = new Size(100, 20);
        lblSource.TabIndex = 0;
        lblSource.Text = "Source Directory:";
        
        // txtSourceDirectory
        txtSourceDirectory.Location = new Point(12, 38);
        txtSourceDirectory.Name = "txtSourceDirectory";
        txtSourceDirectory.ReadOnly = true;
        txtSourceDirectory.Size = new Size(500, 27);
        txtSourceDirectory.TabIndex = 1;
        
        // btnSelectSource
        btnSelectSource.Location = new Point(518, 37);
        btnSelectSource.Name = "btnSelectSource";
        btnSelectSource.Size = new Size(94, 29);
        btnSelectSource.TabIndex = 2;
        btnSelectSource.Text = "Browse...";
        btnSelectSource.UseVisualStyleBackColor = true;
        btnSelectSource.Click += btnSelectSource_Click;
        
        // lblDestination
        lblDestination.AutoSize = true;
        lblDestination.Location = new Point(12, 80);
        lblDestination.Name = "lblDestination";
        lblDestination.Size = new Size(200, 20);
        lblDestination.TabIndex = 3;
        lblDestination.Text = "Destination Directory (optional):";
        
        // txtDestinationDirectory
        txtDestinationDirectory.Location = new Point(12, 103);
        txtDestinationDirectory.Name = "txtDestinationDirectory";
        txtDestinationDirectory.ReadOnly = true;
        txtDestinationDirectory.Size = new Size(500, 27);
        txtDestinationDirectory.TabIndex = 4;
        
        // btnSelectDestination
        btnSelectDestination.Location = new Point(518, 102);
        btnSelectDestination.Name = "btnSelectDestination";
        btnSelectDestination.Size = new Size(94, 29);
        btnSelectDestination.TabIndex = 5;
        btnSelectDestination.Text = "Browse...";
        btnSelectDestination.UseVisualStyleBackColor = true;
        btnSelectDestination.Click += btnSelectDestination_Click;
        
        // lblDuplicateHandling
        lblDuplicateHandling.AutoSize = true;
        lblDuplicateHandling.Location = new Point(12, 145);
        lblDuplicateHandling.Name = "lblDuplicateHandling";
        lblDuplicateHandling.Size = new Size(120, 20);
        lblDuplicateHandling.TabIndex = 6;
        lblDuplicateHandling.Text = "Duplicate Handling:";
        
        // cmbDuplicateHandling
        cmbDuplicateHandling.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbDuplicateHandling.Location = new Point(12, 168);
        cmbDuplicateHandling.Name = "cmbDuplicateHandling";
        cmbDuplicateHandling.Size = new Size(200, 28);
        cmbDuplicateHandling.TabIndex = 7;
        
        // chkRecursive
        chkRecursive.AutoSize = true;
        chkRecursive.Checked = true;
        chkRecursive.CheckState = CheckState.Checked;
        chkRecursive.Location = new Point(230, 170);
        chkRecursive.Name = "chkRecursive";
        chkRecursive.Size = new Size(163, 24);
        chkRecursive.TabIndex = 8;
        chkRecursive.Text = "Process subdirectories";
        chkRecursive.UseVisualStyleBackColor = true;
        
        // chkPreserveOriginals
        chkPreserveOriginals.AutoSize = true;
        chkPreserveOriginals.Location = new Point(410, 170);
        chkPreserveOriginals.Name = "chkPreserveOriginals";
        chkPreserveOriginals.Size = new Size(145, 24);
        chkPreserveOriginals.TabIndex = 9;
        chkPreserveOriginals.Text = "Copy (don't move)";
        chkPreserveOriginals.UseVisualStyleBackColor = true;

        // btnProcess
        btnProcess.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        btnProcess.Location = new Point(12, 210);
        btnProcess.Name = "btnProcess";
        btnProcess.Size = new Size(150, 35);
        btnProcess.TabIndex = 10;
        btnProcess.Text = "Start Processing";
        btnProcess.UseVisualStyleBackColor = true;
        btnProcess.Click += btnProcess_Click;
        
        // btnCancel
        btnCancel.Enabled = false;
        btnCancel.Location = new Point(168, 210);
        btnCancel.Name = "btnCancel";
        btnCancel.Size = new Size(94, 35);
        btnCancel.TabIndex = 11;
        btnCancel.Text = "Cancel";
        btnCancel.UseVisualStyleBackColor = true;
        btnCancel.Click += btnCancel_Click;
        
        // progressBar
        progressBar.Location = new Point(12, 260);
        progressBar.Name = "progressBar";
        progressBar.Size = new Size(600, 29);
        progressBar.TabIndex = 12;
        
        // lblStatus
        lblStatus.AutoSize = true;
        lblStatus.Location = new Point(12, 300);
        lblStatus.Name = "lblStatus";
        lblStatus.Size = new Size(49, 20);
        lblStatus.TabIndex = 13;
        lblStatus.Text = "Ready";
        
        // MainForm
        AutoScaleDimensions = new SizeF(8F, 20F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(624, 341);
        Controls.Add(lblStatus);
        Controls.Add(progressBar);
        Controls.Add(btnCancel);
        Controls.Add(btnProcess);
        Controls.Add(chkPreserveOriginals);
        Controls.Add(chkRecursive);
        Controls.Add(cmbDuplicateHandling);
        Controls.Add(lblDuplicateHandling);
        Controls.Add(btnSelectDestination);
        Controls.Add(txtDestinationDirectory);
        Controls.Add(lblDestination);
        Controls.Add(btnSelectSource);
        Controls.Add(txtSourceDirectory);
        Controls.Add(lblSource);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Photo Manager";
        ResumeLayout(false);
        PerformLayout();
    }
}