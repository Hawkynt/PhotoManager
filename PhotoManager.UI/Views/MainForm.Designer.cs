namespace PhotoManager.UI.Views;

partial class MainForm {
    private System.ComponentModel.IContainer components = null;
    
    // Menu
    private MenuStrip menuStrip;
    private ToolStripMenuItem helpToolStripMenuItem;
    private ToolStripMenuItem aboutToolStripMenuItem;
    
    // Main layout
    private SplitContainer mainSplitContainer;
    private SplitContainer rightSplitContainer;
    private SplitContainer previewMetadataSplitContainer;
    
    // Left panel - TreeView
    private TreeView treeViewSources;
    private ContextMenuStrip treeViewContextMenu;
    private ToolStripMenuItem addPathToolStripMenuItem;
    private ToolStripMenuItem removePathToolStripMenuItem;
    private ToolStripMenuItem toggleRecursiveToolStripMenuItem;
    
    // Center panel - DataGridView
    private DataGridView dataGridViewFiles;
    private DataGridViewTextBoxColumn columnSourceFile;
    private DataGridViewTextBoxColumn columnTargetLocation;
    private DataGridViewTextBoxColumn columnDateSource;
    
    // Right panel - Image preview and metadata
    private PictureBox pictureBoxPreview;
    private Controls.TintedMetadataView tintedMetadataView;
    
    // Bottom panel - Controls
    private FlowLayoutPanel flowLayoutPanelControls;
    private Label lblOutputPath;
    private TextBox txtOutputPath;
    private Button btnSelectOutput;
    private Button btnScan;
    private Button btnRun;
    private Button btnCancel;
    private ComboBox cmbDuplicateHandling;
    private CheckBox chkPreserveOriginals;
    
    // Status bar
    private StatusStrip statusStrip;
    private ToolStripProgressBar toolStripProgressBar;
    private ToolStripStatusLabel toolStripStatusLabel;

    protected override void Dispose(bool disposing) {
        if (disposing && (components != null)) {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent() {
        // Initialize all components
        InitializeControls();
        InitializeLayout();
        InitializeDataGridView();
        InitializeMenus();
        InitializeEvents();
        
        SuspendLayout();
        SetupMainForm();
        ResumeLayout(false);
        PerformLayout();
    }
    
    private void InitializeControls() {
        // Menu
        menuStrip = new MenuStrip();
        helpToolStripMenuItem = new ToolStripMenuItem();
        aboutToolStripMenuItem = new ToolStripMenuItem();
        
        // Split containers
        mainSplitContainer = new SplitContainer();
        rightSplitContainer = new SplitContainer();
        previewMetadataSplitContainer = new SplitContainer();
        
        // TreeView and context menu
        treeViewSources = new TreeView();
        treeViewContextMenu = new ContextMenuStrip();
        addPathToolStripMenuItem = new ToolStripMenuItem();
        removePathToolStripMenuItem = new ToolStripMenuItem();
        toggleRecursiveToolStripMenuItem = new ToolStripMenuItem();
        
        // DataGridView
        dataGridViewFiles = new DataGridView();
        columnSourceFile = new DataGridViewTextBoxColumn();
        columnTargetLocation = new DataGridViewTextBoxColumn();
        columnDateSource = new DataGridViewTextBoxColumn();
        
        // Preview and metadata
        pictureBoxPreview = new PictureBox();
        tintedMetadataView = new Controls.TintedMetadataView();
        
        // Control panel
        flowLayoutPanelControls = new FlowLayoutPanel();
        lblOutputPath = new Label();
        txtOutputPath = new TextBox();
        btnSelectOutput = new Button();
        btnScan = new Button();
        btnRun = new Button();
        btnCancel = new Button();
        cmbDuplicateHandling = new ComboBox();
        chkPreserveOriginals = new CheckBox();
        
        // Status bar
        statusStrip = new StatusStrip();
        toolStripProgressBar = new ToolStripProgressBar();
        toolStripStatusLabel = new ToolStripStatusLabel();
    }
    
    private void InitializeLayout() {
        mainSplitContainer.SuspendLayout();
        rightSplitContainer.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)(dataGridViewFiles)).BeginInit();
        ((System.ComponentModel.ISupportInitialize)(pictureBoxPreview)).BeginInit();
        
        // Main split container (vertical)
        mainSplitContainer.Dock = DockStyle.Fill;
        mainSplitContainer.TabIndex = 0;
        mainSplitContainer.Panel1.Controls.Add(treeViewSources);
        mainSplitContainer.Panel2.Controls.Add(rightSplitContainer);
        
        // Right split container (vertical)
        rightSplitContainer.Dock = DockStyle.Fill;
        rightSplitContainer.TabIndex = 0;
        rightSplitContainer.Panel1.Controls.Add(dataGridViewFiles);
        
        // Right panel split (horizontal)
        previewMetadataSplitContainer.Dock = DockStyle.Fill;
        previewMetadataSplitContainer.Orientation = Orientation.Horizontal;
        previewMetadataSplitContainer.Panel1.Controls.Add(pictureBoxPreview);
        previewMetadataSplitContainer.Panel2.Controls.Add(tintedMetadataView);
        rightSplitContainer.Panel2.Controls.Add(previewMetadataSplitContainer);
        
        // TreeView
        treeViewSources.Dock = DockStyle.Fill;
        treeViewSources.ContextMenuStrip = treeViewContextMenu;
        treeViewSources.CheckBoxes = true;
        treeViewSources.ShowLines = true;
        treeViewSources.ShowPlusMinus = true;
        treeViewSources.ShowRootLines = true;
        treeViewSources.ShowNodeToolTips = true;
        
        // Picture box
        pictureBoxPreview.Dock = DockStyle.Fill;
        pictureBoxPreview.SizeMode = PictureBoxSizeMode.Zoom;
        pictureBoxPreview.BackColor = Color.White;
        pictureBoxPreview.BorderStyle = BorderStyle.Fixed3D;
        
        // Tinted metadata view
        tintedMetadataView.Dock = DockStyle.Fill;
        
        // Control panel
        flowLayoutPanelControls.Dock = DockStyle.Bottom;
        flowLayoutPanelControls.Height = 80;
        flowLayoutPanelControls.FlowDirection = FlowDirection.LeftToRight;
        flowLayoutPanelControls.WrapContents = true;
        flowLayoutPanelControls.Padding = new Padding(10);
        
        // First row - Output path
        lblOutputPath.Text = Resources.Strings.OutputPath_Label;
        lblOutputPath.AutoSize = true;
        lblOutputPath.Anchor = AnchorStyles.Left;
        lblOutputPath.Margin = new Padding(0, 6, 5, 3);
        
        txtOutputPath.Width = 300;
        txtOutputPath.PlaceholderText = Resources.Strings.OutputPath_Placeholder;
        txtOutputPath.Margin = new Padding(0, 3, 5, 3);
        
        btnSelectOutput.Text = Resources.Strings.Button_SelectOutput;
        btnSelectOutput.Size = new Size(120, 30);
        btnSelectOutput.Margin = new Padding(0, 3, 10, 3);
        
        // Second row - Action buttons and options
        flowLayoutPanelControls.SetFlowBreak(btnSelectOutput, true);
        
        flowLayoutPanelControls.Controls.Add(lblOutputPath);
        flowLayoutPanelControls.Controls.Add(txtOutputPath);
        flowLayoutPanelControls.Controls.Add(btnSelectOutput);
        flowLayoutPanelControls.Controls.Add(btnScan);
        flowLayoutPanelControls.Controls.Add(btnRun);
        flowLayoutPanelControls.Controls.Add(btnCancel);
        flowLayoutPanelControls.Controls.Add(cmbDuplicateHandling);
        flowLayoutPanelControls.Controls.Add(chkPreserveOriginals);
        
        // Buttons
        btnScan.Size = new Size(100, 30);
        btnScan.UseVisualStyleBackColor = true;
        
        btnRun.Size = new Size(100, 30);
        btnRun.UseVisualStyleBackColor = true;
        btnRun.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        
        btnCancel.Size = new Size(80, 30);
        btnCancel.UseVisualStyleBackColor = true;
        btnCancel.Enabled = false;
        
        // ComboBox
        cmbDuplicateHandling.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbDuplicateHandling.Size = new Size(150, 30);
        
        // CheckBox
        chkPreserveOriginals.AutoSize = true;
        chkPreserveOriginals.UseVisualStyleBackColor = true;
        
        // Status strip
        statusStrip.Items.Add(toolStripStatusLabel);
        statusStrip.Items.Add(toolStripProgressBar);
        toolStripProgressBar.Size = new Size(200, 16);
        toolStripStatusLabel.Spring = true;
        toolStripStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
    }
    
    private void InitializeDataGridView() {
        dataGridViewFiles.Dock = DockStyle.Fill;
        dataGridViewFiles.AllowUserToAddRows = false;
        dataGridViewFiles.AllowUserToDeleteRows = false;
        dataGridViewFiles.ReadOnly = true;
        dataGridViewFiles.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        dataGridViewFiles.MultiSelect = false;
        dataGridViewFiles.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        dataGridViewFiles.RowHeadersVisible = false;
        dataGridViewFiles.AllowUserToResizeRows = false;
        dataGridViewFiles.AllowUserToResizeColumns = true;
        
        // Columns - configured for data binding
        columnSourceFile.HeaderText = "File Name";
        columnSourceFile.Name = "FileName";
        columnSourceFile.DataPropertyName = "FileName";
        columnSourceFile.FillWeight = 30;
        
        columnTargetLocation.HeaderText = "Target Location";
        columnTargetLocation.Name = "TargetLocation";
        columnTargetLocation.DataPropertyName = "TargetLocation";
        columnTargetLocation.FillWeight = 40;
        
        columnDateSource.HeaderText = "Source Path";
        columnDateSource.Name = "SourcePath";
        columnDateSource.DataPropertyName = "SourcePath";
        columnDateSource.FillWeight = 30;
        
        dataGridViewFiles.Columns.AddRange(new DataGridViewColumn[] {
            columnSourceFile,
            columnTargetLocation,
            columnDateSource
        });
    }
    
    private void InitializeMenus() {
        // Main menu
        helpToolStripMenuItem.Text = Resources.Strings.Menu_Help;
        helpToolStripMenuItem.DropDownItems.Add(aboutToolStripMenuItem);
        
        aboutToolStripMenuItem.Text = Resources.Strings.Menu_About;
        aboutToolStripMenuItem.Click += AboutToolStripMenuItem_Click;
        
        menuStrip.Items.Add(helpToolStripMenuItem);
        
        // TreeView context menu
        addPathToolStripMenuItem.Text = Resources.Strings.TreeView_AddPath;
        addPathToolStripMenuItem.Click += AddPathToolStripMenuItem_Click;
        
        removePathToolStripMenuItem.Text = Resources.Strings.TreeView_RemovePath;
        removePathToolStripMenuItem.Click += RemovePathToolStripMenuItem_Click;
        
        toggleRecursiveToolStripMenuItem.Text = Resources.Strings.TreeView_ToggleRecursive;
        toggleRecursiveToolStripMenuItem.Click += ToggleRecursiveToolStripMenuItem_Click;
        
        treeViewContextMenu.Items.AddRange(new ToolStripItem[] {
            addPathToolStripMenuItem,
            removePathToolStripMenuItem,
            new ToolStripSeparator(),
            toggleRecursiveToolStripMenuItem
        });
    }
    
    private void InitializeEvents() {
        btnScan.Click += BtnScan_Click;
        btnRun.Click += BtnRun_Click;
        btnCancel.Click += BtnCancel_Click;
        btnSelectOutput.Click += BtnSelectOutput_Click;
        dataGridViewFiles.SelectionChanged += DataGridViewFiles_SelectionChanged;
        treeViewSources.AfterCheck += TreeViewSources_AfterCheck;
        pictureBoxPreview.DoubleClick += PictureBoxPreview_DoubleClick;
    }
    
    private void SetupMainForm() {
        // Set localized text
        btnScan.Text = Resources.Strings.Button_Scan;
        btnRun.Text = Resources.Strings.Button_Run;
        btnCancel.Text = Resources.Strings.Cancel;
        chkPreserveOriginals.Text = Resources.Strings.CheckBox_PreserveOriginals;
        toolStripStatusLabel.Text = Resources.Strings.Ready;
        
        // Main form properties
        AutoScaleDimensions = new SizeF(8F, 20F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1200, 800);
        MainMenuStrip = menuStrip;
        MinimumSize = new Size(800, 600);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = Resources.Strings.ApplicationTitle;
        
        // Add controls to form
        Controls.Add(mainSplitContainer);
        Controls.Add(flowLayoutPanelControls);
        Controls.Add(menuStrip);
        Controls.Add(statusStrip);
        
        mainSplitContainer.ResumeLayout(false);
        rightSplitContainer.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)(dataGridViewFiles)).EndInit();
        ((System.ComponentModel.ISupportInitialize)(pictureBoxPreview)).EndInit();
    }
}