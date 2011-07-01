﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace QuickOpenFile
{
    /// <summary>
    /// Summary description for MyControl.
    /// </summary>
    public partial class QuickOpenFileControl : UserControl, IVsUIWin32Element
    {
        private Options options;
        private SearchEngine searchEngine;
        public QuickOpenFileToolWindow parentWindowPane;
        public string applicationRegistryKey;
        bool doIndex;
        IEnumerable<SolutionFile> resultsRest;

        public QuickOpenFileControl()
        {
            InitializeComponent();
            options = new Options();
            options.Load(applicationRegistryKey);
            searchEngine = new SearchEngine(this, options.AsynchronousSearch);
            doIndex = false;
        }

        // This method is called every time the Quick Open File command is
        // invoked from the IDE. Creates an index of all solution items for
        // subsequent incremental searches. Focuses the search text box.
        public void InitControl()
        {
            Focus();
            uxSearch.Focus();
            uxSearch.SelectAll();
            System.Diagnostics.Debug.Print("QOFControl.InitOnShow() " + this.Visible);

            // (re)index the solution and repeat last search
            ShowResults(null);
            uxFiles.CheckBoxes = options.ShowCheckboxes;
            SearchAfterWhile(true, options.LongKeystrokeDelay);

            ActiveControl = uxSearch;
        }

        public void CleanUp()
        {
            // Terminate search engine thread.
            searchEngine.Stop();
            options.Save(applicationRegistryKey);
        }

        private bool ShouldLoadAllResults()
        {
            if (options.ResultsLimit > 0 && uxFiles.SelectedItems.Count > 0 && IsLoadPlaceholder(uxFiles.SelectedItems[0]))
                return true;
            else
                return false;
        }

        private IEnumerable<SolutionFile> GetSelectedSolutionFiles()
        {
            IEnumerable<ListViewItem> files;
            if (uxFiles.CheckedItems.Count > 0)
                files = uxFiles.CheckedItems.Cast<ListViewItem>();
            else
                files = uxFiles.SelectedItems.Cast<ListViewItem>();

            return files.
                Where(i => i.Tag != null).
                Select(i => i.Tag as SolutionFile);
        }

        private bool OpenSolutionResource(SolutionFile sr, string editor)
        {
            if (sr == null) return false;
            if (string.IsNullOrEmpty(sr.FilePath)) return false;
            IVsCommandWindow cmw = (IVsCommandWindow)GetService(typeof(SVsCommandWindow));
            if (cmw == null) return false;

            if (editor == null)
                cmw.ExecuteCommand("of \"" + sr.FilePath + "\"");
            else if (editor.Length == 0)
                cmw.ExecuteCommand("of \"" + sr.FilePath + "\" /editor");
            else
                cmw.ExecuteCommand("of \"" + sr.FilePath + "\" /e:\"" + editor + "\"");

            return true;
        }

        private void OpenSelectedFiles(string editor = null)
        {
            if (ShouldLoadAllResults())
            {
                // load rest of search results
                ShowResultsRest(resultsRest);
            }
            else
            {
                bool success = true;

                foreach (var file in GetSelectedSolutionFiles())
                {
                    if (!OpenSolutionResource(file, editor))
                        success = false;
                }

                if (success)
                    parentWindowPane.HideToolWindow();
            }
        }

        // Used to update result list from another thread.
        public void ShowResults(IEnumerable<SolutionFile> results)
        {
            uxFiles.BeginUpdate();
            uxFiles.Items.Clear();

            if (results != null && options.ResultsLimit > 0 && results.Count() > options.ResultsLimit)
            {
                resultsRest = results.Skip(options.ResultsLimit);
                results = results.Take(options.ResultsLimit);
            }
            else
                resultsRest = null;

            DateTime time1 = DateTime.Now;
            // add results
            if (results != null)
            {
                foreach (SolutionFile sr in results)
                    uxFiles.Items.Add(sr.Item);
            }
            // Add 'show more items' line
            if (resultsRest != null)
            {
                uxFiles.Items.Add(MakeLoadPlaceholder(resultsRest.Count()));
            }
            DateTime time2 = DateTime.Now;
            System.Diagnostics.Debug.Print("QOF: ListBox populated in " + (time2 - time1));

            uxFiles.EndUpdate();
            if (uxFiles.Items.Count > 0)
                uxFiles.Items[0].Selected = true;
        }

        public void ShowResultsRest(IEnumerable<SolutionFile> results)
        {
            int selectedIndex = uxFiles.SelectedIndices.Count > 0 ? uxFiles.SelectedIndices[0] : -1;
            uxFiles.BeginUpdate();
            uxFiles.Items.RemoveAt(uxFiles.Items.Count - 1);

            DateTime time1 = DateTime.Now;
            if (results != null)
            {
                foreach (SolutionFile sr in results)
                    uxFiles.Items.Add(sr.Item);
            }
            DateTime time2 = DateTime.Now;
            System.Diagnostics.Debug.Print("QOF: ListBox populated in " + (time2 - time1));

            uxFiles.EndUpdate();
            if (selectedIndex >= 0 && selectedIndex < uxFiles.Items.Count)
                uxFiles.Items[selectedIndex].Selected = true;
        }

        private ListViewItem MakeLoadPlaceholder(int itemsNotVisible)
        {
            ListViewItem lvi = new ListViewItem("... and " + itemsNotVisible + " more items");
            lvi.ForeColor = System.Drawing.SystemColors.GrayText;
            lvi.Font = new System.Drawing.Font(lvi.Font, System.Drawing.FontStyle.Italic);
            return lvi;
        }

        private bool IsLoadPlaceholder(ListViewItem lvi)
        {
            return lvi.Tag == null && lvi.SubItems.Count == 1;
        }

        // Used to update status text from another thread.
        public void SetStatusText(string text)
        {
            uxStatus.Text = text;
        }

        public IVsSolution GetSolution()
        {
            return (IVsSolution)GetService(typeof(SVsSolution));
        }

        /// <summary>
        /// Searches the solution for the search expression from the textbox.
        /// </summary>
        /// <param name="index">Reindexes the solution before search.</param>
        /// <param name="delay">Delay before the actual search.</param>
        private void SearchAfterWhile(bool index = false, int delay = -1)
        {
            // Waits a fraction of a second for further keystrokes. Then begins
            // the search. This delay is to avoid intensive searching after each
            // keystroke, which used to produce noticable slowdown of the UI.
            doIndex = doIndex | index;

            if (delay < 0)
                delay = uxSearch.Text.Trim().Length > 2 ? options.ShortKeystrokeDelay : options.LongKeystrokeDelay;

            if (delay <= 0 || (doIndex && options.AsynchronousSearch))
                SearchNow();
            else
            {
                uxTimer.Stop();
                uxTimer.Interval = delay;
                uxTimer.Start();
            }
        }

        private void SearchNow()
        {
            // This performs the actual search.
            uxTimer.Stop();
            searchEngine.Search(uxSearch.Text.Trim(), options, doIndex);
            doIndex = false;
        }

        #region UI Code

        private void uxTimer_Tick(object sender, EventArgs e)
        {
            SearchNow();
        }

        private void uxSearch_TextChanged(object sender, System.EventArgs e)
        {
            System.Diagnostics.Debug.Print("QOF: Key strokes typed...");
            SearchAfterWhile();
        }

        private void uxSearch_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Down:
                    //TODO: start search immediately if not yet started (ie. waiting for keystrokes)
                    uxFiles.Focus();
                    e.SuppressKeyPress = true;
                    break;
                case Keys.PageDown:
                    //TODO: start search immediately if not yet started (ie. waiting for keystrokes)
                    uxFiles.Focus();
                    SendKeys.Send("{PGDN}");
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Up:
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Return:
                    OpenSelectedFiles(e.Shift ? string.Empty : null);
                    e.SuppressKeyPress = true;
                    break;
            }
        }

        private void uxFiles_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar != ' ')
            {
                uxSearch.Focus();
                SendKeys.Send(e.KeyChar.ToString());
                e.Handled = true;
            }
            else
            {
                e.Handled = false;
            }
        }

        private void uxFiles_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.PageUp:
                case Keys.Up:
                    if (uxFiles.Items.Count == 0 || uxFiles.SelectedIndices.Contains(0))
                    {
                        uxSearch.Focus();
                        uxSearch.SelectAll();
                        e.SuppressKeyPress = true;
                    }
                    break;
                case Keys.Return:
                    OpenSelectedFiles(e.Shift ? string.Empty : null);
                    e.SuppressKeyPress = true;
                    break;
                default:
                    e.SuppressKeyPress = false;
                    break;
            }
        }


        private void uxOpen_Click(object sender, EventArgs e)
        {
            OpenSelectedFiles();
        }

        private void uxFiles_DoubleClick(object sender, EventArgs e)
        {
            OpenSelectedFiles();
        }

        private void uxFiles_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            uxStatus.Text = "";
            uxOpen.Enabled = false;

            if (uxFiles.SelectedItems.Count > 0)
            {
                if (IsLoadPlaceholder(uxFiles.SelectedItems[0]))
                {
                    uxStatus.Text = "Press Enter to show all results.";
                }
                else if (uxFiles.SelectedItems[0].Tag != null)
                {
                    SolutionFile sr = (SolutionFile)uxFiles.SelectedItems[0].Tag;
                    uxStatus.Text = sr.FilePath;
                    uxOpen.Enabled = true;
                }
            }
        }

        private void uxOpenWith_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (uxFiles.SelectedItems.Count == 0)
                e.Cancel = true;
            else
            {
                //TODO: maybe directly fill in list of available editors for the item in the future
                openWithToolStripMenuItem.DropDownItems.Clear();
            }
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenSelectedFiles();
        }

        private void openWithToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenSelectedFiles(string.Empty);
        }

        private void uxOpenWith_MouseDown(object sender, MouseEventArgs e)
        {
            uxOpenWithMenu.Show(uxOpen, new System.Drawing.Point(0, 0));
        }

        private void uxOpen_EnabledChanged(object sender, EventArgs e)
        {
            uxOpenWith.Enabled = uxOpen.Enabled;
        }

        private void uxOptions_Click(object sender, EventArgs e)
        {
            OptionsDialog dlg = new OptionsDialog(applicationRegistryKey);
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                // settings may changed: reload options
                options.LoadDefaults();
                options.Load(applicationRegistryKey);
                // reindex solution, repeat last search
                InitControl();
            }
            ActiveControl = uxSearch;
        }

        #endregion

        #region IVsUIWin32Element Members

        [DllImport("user32.dll")]
        static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        public int Create(IntPtr parent, out IntPtr pHandle)
        {
            pHandle = this.Handle;
            SetParent(this.Handle, parent);
            return 1;
            //throw new NotImplementedException();
        }

        public int Destroy()
        {
            return 1;
            //throw new NotImplementedException();
        }

        public int GetHandle(out IntPtr pHandle)
        {
            pHandle = this.Handle;
            return 1;
        }

        public int ShowModal(IntPtr parent, out int pDlgResult)
        {
            throw new NotImplementedException();
        }

        #endregion

    }
}
