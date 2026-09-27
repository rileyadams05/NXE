namespace NXEUsbSetupTool
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.ComboBox driveBox;
        private System.Windows.Forms.Button refreshButton;
        private System.Windows.Forms.Button prepareButton;
        private System.Windows.Forms.ProgressBar progressBar1;
        private System.Windows.Forms.Label statusLabel;
        private System.Windows.Forms.Label headerLabel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.headerLabel = new System.Windows.Forms.Label();
            this.driveBox = new System.Windows.Forms.ComboBox();
            this.refreshButton = new System.Windows.Forms.Button();
            this.prepareButton = new System.Windows.Forms.Button();
            this.progressBar1 = new System.Windows.Forms.ProgressBar();
            this.statusLabel = new System.Windows.Forms.Label();
            this.SuspendLayout();
            // 
            // headerLabel
            // 
            this.headerLabel.AutoSize = true;
            this.headerLabel.Font = new System.Drawing.Font("Segoe UI", 9.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.headerLabel.Location = new System.Drawing.Point(20, 15);
            this.headerLabel.Name = "headerLabel";
            this.headerLabel.Size = new System.Drawing.Size(188, 17);
            this.headerLabel.TabIndex = 5;
            this.headerLabel.Text = "Target NTFS External Drive:";
            // 
            // driveBox
            // 
            this.driveBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.driveBox.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.driveBox.FormattingEnabled = true;
            this.driveBox.Location = new System.Drawing.Point(20, 40);
            this.driveBox.Name = "driveBox";
            this.driveBox.Size = new System.Drawing.Size(325, 23);
            this.driveBox.TabIndex = 0;
            // 
            // refreshButton
            // 
            this.refreshButton.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.refreshButton.Location = new System.Drawing.Point(355, 38);
            this.refreshButton.Name = "refreshButton";
            this.refreshButton.Size = new System.Drawing.Size(75, 27);
            this.refreshButton.TabIndex = 1;
            this.refreshButton.Text = "Refresh";
            this.refreshButton.UseVisualStyleBackColor = true;
            this.refreshButton.Click += new System.EventHandler(this.RefreshButton_Click);
            // 
            // prepareButton
            // 
            this.prepareButton.Font = new System.Drawing.Font("Segoe UI", 9.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.prepareButton.Location = new System.Drawing.Point(20, 80);
            this.prepareButton.Name = "prepareButton";
            this.prepareButton.Size = new System.Drawing.Size(410, 36);
            this.prepareButton.TabIndex = 2;
            this.prepareButton.Text = "Prepare Drive for NXE";
            this.prepareButton.UseVisualStyleBackColor = true;
            this.prepareButton.Click += new System.EventHandler(this.PrepareButton_Click);
            // 
            // progressBar1
            // 
            this.progressBar1.Location = new System.Drawing.Point(20, 130);
            this.progressBar1.Name = "progressBar1";
            this.progressBar1.Size = new System.Drawing.Size(410, 23);
            this.progressBar1.TabIndex = 3;
            // 
            // statusLabel
            // 
            this.statusLabel.AutoEllipsis = true;
            this.statusLabel.Font = new System.Drawing.Font("Segoe UI", 8.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.statusLabel.Location = new System.Drawing.Point(20, 162);
            this.statusLabel.Name = "statusLabel";
            this.statusLabel.Size = new System.Drawing.Size(410, 30);
            this.statusLabel.TabIndex = 4;
            this.statusLabel.Text = "Ready";
            // 
            // Form1
            // 
            this.ClientSize = new System.Drawing.Size(450, 205);
            this.Controls.Add(this.headerLabel);
            this.Controls.Add(this.statusLabel);
            this.Controls.Add(this.progressBar1);
            this.Controls.Add(this.prepareButton);
            this.Controls.Add(this.refreshButton);
            this.Controls.Add(this.driveBox);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.Name = "Form1";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "NXE USB Setup Tool";
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
