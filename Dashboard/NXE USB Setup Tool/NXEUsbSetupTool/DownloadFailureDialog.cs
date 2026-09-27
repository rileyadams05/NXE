using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace NXEUsbSetupTool
{
    internal sealed class DownloadFailureDialog : Form
    {
        public bool RetryRequested { get; private set; }

        public DownloadFailureDialog(IReadOnlyList<EmulatorPackageFailure> failures,
            IReadOnlyCollection<string> readyIds)
        {
            Text = "Emulator Package Download";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(540, 330);
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.White;

            var message = new Label
            {
                AutoSize = false,
                Location = new Point(18, 16),
                Size = new Size(504, 48),
                ForeColor = Color.White,
                Text = string.Join(Environment.NewLine, failures.Select(failure =>
                    "Could not download " + failure.Definition.DisplayName + "."))
            };
            Controls.Add(message);

            var status = new TextBox
            {
                Location = new Point(18, 72),
                Size = new Size(504, 198),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Text = BuildStatus(failures, readyIds)
            };
            Controls.Add(status);

            var retry = new Button { Text = "Retry", Location = new Point(340, 286), Size = new Size(86, 28) };
            retry.Click += (_, __) => { RetryRequested = true; DialogResult = DialogResult.Retry; Close(); };
            Controls.Add(retry);

            var continueButton = new Button { Text = "Continue", Location = new Point(436, 286), Size = new Size(86, 28) };
            continueButton.Click += (_, __) => { RetryRequested = false; DialogResult = DialogResult.Ignore; Close(); };
            Controls.Add(continueButton);
            AcceptButton = retry;
            CancelButton = continueButton;
        }

        private static string BuildStatus(IReadOnlyList<EmulatorPackageFailure> failures,
            IReadOnlyCollection<string> readyIds)
        {
            var failed = new HashSet<string>(failures.Select(item => item.Definition.Id), StringComparer.OrdinalIgnoreCase);
            return string.Join(Environment.NewLine,
                NxeDashboard.Shared.EmulatorPackageManifest.Definitions.Select(definition =>
                    definition.DisplayName.PadRight(22) +
                    (readyIds.Contains(definition.Id) ? "Ready" : failed.Contains(definition.Id) ? "Download failed" : "Missing")));
        }
    }
}
