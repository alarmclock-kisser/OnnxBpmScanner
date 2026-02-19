namespace OnnxBpmScanner.Forms
{
    partial class WindowMain
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.listBox_audios = new ListBox();
            this.contextMenuStrip_audios = new ContextMenuStrip(this.components);
            this.removeToolStripMenuItem = new ToolStripMenuItem();
            this.progressBar_inferencing = new ProgressBar();
            this.button_import = new Button();
            this.button_analyze = new Button();
            this.label_elapsed = new Label();
            this.button_browse = new Button();
            this.textBox_directory = new TextBox();
            this.listBox_log = new ListBox();
            this.comboBox_devices = new ComboBox();
            this.button_initialize = new Button();
            this.checkBox_writeTags = new CheckBox();
            this.button_clear = new Button();
            this.contextMenuStrip_audios.SuspendLayout();
            this.SuspendLayout();
            // 
            // listBox_audios
            // 
            this.listBox_audios.ContextMenuStrip = this.contextMenuStrip_audios;
            this.listBox_audios.FormattingEnabled = true;
            this.listBox_audios.Location = new Point(12, 231);
            this.listBox_audios.Name = "listBox_audios";
            this.listBox_audios.Size = new Size(340, 169);
            this.listBox_audios.TabIndex = 0;
            this.listBox_audios.MouseDown += this.listBox_audios_MouseDown;
            // 
            // contextMenuStrip_audios
            // 
            this.contextMenuStrip_audios.Items.AddRange(new ToolStripItem[] { this.removeToolStripMenuItem });
            this.contextMenuStrip_audios.Name = "contextMenuStrip_audios";
            this.contextMenuStrip_audios.Size = new Size(118, 26);
            // 
            // removeToolStripMenuItem
            // 
            this.removeToolStripMenuItem.Name = "removeToolStripMenuItem";
            this.removeToolStripMenuItem.Size = new Size(117, 22);
            this.removeToolStripMenuItem.Text = "Remove";
            this.removeToolStripMenuItem.Click += this.removeToolStripMenuItem_Click;
            // 
            // progressBar_inferencing
            // 
            this.progressBar_inferencing.Location = new Point(12, 406);
            this.progressBar_inferencing.Maximum = 1000;
            this.progressBar_inferencing.Name = "progressBar_inferencing";
            this.progressBar_inferencing.Size = new Size(420, 23);
            this.progressBar_inferencing.TabIndex = 1;
            // 
            // button_import
            // 
            this.button_import.Location = new Point(358, 231);
            this.button_import.Name = "button_import";
            this.button_import.Size = new Size(74, 23);
            this.button_import.TabIndex = 2;
            this.button_import.Text = "Import";
            this.button_import.UseVisualStyleBackColor = true;
            this.button_import.Click += this.button_import_Click;
            // 
            // button_analyze
            // 
            this.button_analyze.BackColor = SystemColors.Info;
            this.button_analyze.Location = new Point(358, 377);
            this.button_analyze.Name = "button_analyze";
            this.button_analyze.Size = new Size(74, 23);
            this.button_analyze.TabIndex = 3;
            this.button_analyze.Text = "Analyze";
            this.button_analyze.UseVisualStyleBackColor = false;
            this.button_analyze.Click += this.button_analyze_Click;
            // 
            // label_elapsed
            // 
            this.label_elapsed.AutoSize = true;
            this.label_elapsed.Location = new Point(358, 359);
            this.label_elapsed.Name = "label_elapsed";
            this.label_elapsed.Size = new Size(71, 15);
            this.label_elapsed.TabIndex = 4;
            this.label_elapsed.Text = "Elapsed: -:--";
            // 
            // button_browse
            // 
            this.button_browse.Location = new Point(358, 260);
            this.button_browse.Name = "button_browse";
            this.button_browse.Size = new Size(74, 23);
            this.button_browse.TabIndex = 5;
            this.button_browse.Text = "Browse";
            this.button_browse.UseVisualStyleBackColor = true;
            this.button_browse.Click += this.button_browse_Click;
            // 
            // textBox_directory
            // 
            this.textBox_directory.Location = new Point(438, 260);
            this.textBox_directory.MaxLength = 16384;
            this.textBox_directory.Name = "textBox_directory";
            this.textBox_directory.PlaceholderText = "Audio Files Directory ...";
            this.textBox_directory.Size = new Size(254, 23);
            this.textBox_directory.TabIndex = 6;
            // 
            // listBox_log
            // 
            this.listBox_log.FormattingEnabled = true;
            this.listBox_log.HorizontalScrollbar = true;
            this.listBox_log.Location = new Point(358, 12);
            this.listBox_log.Name = "listBox_log";
            this.listBox_log.Size = new Size(334, 199);
            this.listBox_log.TabIndex = 7;
            this.listBox_log.DoubleClick += this.listBox_log_DoubleClick;
            // 
            // comboBox_devices
            // 
            this.comboBox_devices.FormattingEnabled = true;
            this.comboBox_devices.Location = new Point(12, 12);
            this.comboBox_devices.Name = "comboBox_devices";
            this.comboBox_devices.Size = new Size(259, 23);
            this.comboBox_devices.TabIndex = 8;
            this.comboBox_devices.Text = "DirectML ONNX-Device ...";
            this.comboBox_devices.SelectedIndexChanged += this.comboBox_devices_SelectedIndexChanged;
            // 
            // button_initialize
            // 
            this.button_initialize.Location = new Point(277, 12);
            this.button_initialize.Name = "button_initialize";
            this.button_initialize.Size = new Size(75, 23);
            this.button_initialize.TabIndex = 9;
            this.button_initialize.Text = "Initialize";
            this.button_initialize.UseVisualStyleBackColor = true;
            this.button_initialize.Click += this.button_initialize_Click;
            // 
            // checkBox_writeTags
            // 
            this.checkBox_writeTags.AutoSize = true;
            this.checkBox_writeTags.Checked = true;
            this.checkBox_writeTags.CheckState = CheckState.Checked;
            this.checkBox_writeTags.Location = new Point(438, 380);
            this.checkBox_writeTags.Name = "checkBox_writeTags";
            this.checkBox_writeTags.Size = new Size(81, 19);
            this.checkBox_writeTags.TabIndex = 10;
            this.checkBox_writeTags.Text = "Write Tags";
            this.checkBox_writeTags.UseVisualStyleBackColor = true;
            // 
            // button_clear
            // 
            this.button_clear.BackColor = Color.FromArgb(  255,   192,   192);
            this.button_clear.Location = new Point(358, 333);
            this.button_clear.Name = "button_clear";
            this.button_clear.Size = new Size(75, 23);
            this.button_clear.TabIndex = 11;
            this.button_clear.Text = "Clear";
            this.button_clear.UseVisualStyleBackColor = false;
            this.button_clear.Click += this.button_clear_Click;
            // 
            // WindowMain
            // 
            this.AutoScaleDimensions = new SizeF(7F, 15F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(704, 441);
            this.Controls.Add(this.button_clear);
            this.Controls.Add(this.checkBox_writeTags);
            this.Controls.Add(this.button_initialize);
            this.Controls.Add(this.comboBox_devices);
            this.Controls.Add(this.listBox_log);
            this.Controls.Add(this.textBox_directory);
            this.Controls.Add(this.button_browse);
            this.Controls.Add(this.label_elapsed);
            this.Controls.Add(this.button_analyze);
            this.Controls.Add(this.button_import);
            this.Controls.Add(this.progressBar_inferencing);
            this.Controls.Add(this.listBox_audios);
            this.MaximizeBox = false;
            this.MaximumSize = new Size(720, 480);
            this.MinimumSize = new Size(720, 480);
            this.Name = "WindowMain";
            this.Text = "Onnx BPM Scanner (using DirectML) (Forms UI)";
            this.contextMenuStrip_audios.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private ListBox listBox_audios;
        private ProgressBar progressBar_inferencing;
        private Button button_import;
        private Button button_analyze;
        private Label label_elapsed;
        private Button button_browse;
        private TextBox textBox_directory;
        private ListBox listBox_log;
        private ComboBox comboBox_devices;
        private Button button_initialize;
        private CheckBox checkBox_writeTags;
        private Button button_clear;
        private ContextMenuStrip contextMenuStrip_audios;
        private ToolStripMenuItem removeToolStripMenuItem;
    }
}
