using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using PluginCore.Managers;
using ScintillaNet;
using WeifenLuo.WinFormsUI.Docking;

namespace PluginCore.Controls
{
    public class UITools : IMessageFilter, IEventHandler
    {
        public delegate void CharAddedHandler(ScintillaControl sender, int value);
        public delegate void TextChangedHandler(ScintillaControl sender, int position, int length, int linesAdded);
        public delegate void MouseHoverHandler(ScintillaControl sender, int position);
        public delegate void LineEventHandler(ScintillaControl sender, int line);

        private const Keys ToggleShowDetailsKey = Keys.F1;

        #region Singleton Instance

        private static UITools manager;

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void Initialize()
        {
            if (manager == null)
            {
                manager = new UITools();
            }
        }

        public static UITools Manager
        {
            get { return manager; }
        }

        public static CodeTip CodeTip
        {
            get { return manager.codeTip; }
        }

        public static RichToolTip Tip
        {
            get { return manager.simpleTip; }
        }

        public static MethodCallTip CallTip
        {
            get { return manager.callTip; }
        }

        #endregion

        #region Initialization

        public event MouseHoverHandler OnMouseHover;
        public event MouseHoverHandler OnMouseHoverEnd;
        public event CharAddedHandler OnCharAdded;
        public event TextChangedHandler OnTextChanged;
        public event LineEventHandler OnMarkerChanged;

        public bool DisableEvents;

        /// <summary>
        /// Option: show detailed information in tips.
        /// </summary>
        /// <remarks>
        /// Default value is defined in the main settings.
        /// State is switched using F1 key when a tip is visible.
        /// </remarks>
        public bool ShowDetails
        {
            get { return showDetails; }
            set { showDetails = value; }
        }
        
        private bool ignoreKeys;
        private bool showDetails;
        private CodeTip codeTip;
        private RichToolTip simpleTip;
        private MethodCallTip callTip;

        private UITools()
        {
            ignoreKeys = false;
            showDetails = PluginBase.Settings.ShowDetails;
            //
            // CONTROLS
            //
            try
            {
                CompletionList.CreateControl(PluginBase.MainForm);
                codeTip = new CodeTip(PluginBase.MainForm);
                simpleTip = new RichToolTip(PluginBase.MainForm);
                callTip = new MethodCallTip(PluginBase.MainForm);
            }
            catch(Exception ex)
            {
                ErrorManager.ShowError(/*"Error while creating editor controls.",*/ ex);
            }
            //
            // Shortcuts
            //
            PluginBase.MainForm.RegisterShortcut("Completion.ListMembers", Keys.Control | Keys.Space);
            PluginBase.MainForm.RegisterShortcut("Completion.ParameterInfo", Keys.Control | Keys.Shift | Keys.Space);
            ScintillaControl.InitShortcuts();
            //
            // Events
            //
            PluginBase.MainForm.DockPanel.ActivePaneChanged += new EventHandler(DockPanel_ActivePaneChanged);
            EventManager.AddEventHandler(this, EventType.ShortcutKey, HandlingPriority.High);
            EventManager.AddEventHandler(this, EventType.FileSave | EventType.Command | EventType.Keys);
        }

        #endregion

        private WeakReference lockedSciControl;
        private Point lastMousePos = new Point(0,0);

        #region SciControls & MainForm Events

        private void DockPanel_ActivePaneChanged(object sender, EventArgs e)
        {
            if (PluginBase.MainForm.DockPanel.ActivePane != null 
                && PluginBase.MainForm.DockPanel.ActivePane != PluginBase.MainForm.DockPanel.ActiveDocumentPane)
            {
                OnUIRefresh(null);
            }
        }

        public void HandleEvent(object sender, NotifyEvent e, HandlingPriority priority)
        {
            switch (e.Type)
            {
                case EventType.FileSave:
                    MessageBar.HideWarning();
                    return;

                case EventType.Command:
                    string cmd = (e as DataEvent).Action;
                    if (cmd.IndexOfOrdinal("ProjectManager") > 0
                        || cmd.IndexOfOrdinal("Changed") > 0
                        || cmd.IndexOfOrdinal("Context") > 0
                        || cmd.IndexOfOrdinal("ClassPath") > 0
                        || cmd.IndexOfOrdinal("Watcher") > 0
                        || cmd.IndexOfOrdinal("Get") > 0
                        || cmd.IndexOfOrdinal("Set") > 0
                        || cmd.IndexOfOrdinal("SDK") > 0)
                        return; // ignore notifications
                    break;

                case EventType.ShortcutKey:
                    var ske = (ShortcutKeyEvent) e;
                    var sci = PluginBase.MainForm.CurrentDocument.SciControl;
                    e.Handled = sci != null && sci.IsFocus && sci.HandleShortcut(ske)
                        || HandleShortcut(ske);
                    return;

                case EventType.Keys:
                    e.Handled = HandleKeys(((KeyEvent) e).KeyData);
                    return;
            }
            // most of the time, an event should hide the list
            OnUIRefresh(null);
        }

        /// <summary>
        /// Reserved to MainForm
        /// </summary>
        public void ListenTo(ScintillaControl sci)
        {
            // hook scintilla events
            sci.MouseDwellTime = PluginBase.MainForm.Settings.HoverDelay;
            sci.DwellStart += new DwellStartHandler(HandleDwellStart);
            sci.DwellEnd += new DwellEndHandler(HandleDwellEnd);
            sci.CharAdded += new ScintillaNet.CharAddedHandler(OnChar);
            sci.UpdateUI += new UpdateUIHandler(OnUIRefresh);
            sci.TextInserted += new TextInsertedHandler(OnTextInserted);
            sci.TextDeleted += new TextDeletedHandler(OnTextDeleted);
        }

        /// <summary>
        /// Notify all listeners that document markers were changed
        /// </summary>
        public void MarkerChanged(ScintillaControl sender, int line)
        {
            if (OnMarkerChanged != null) OnMarkerChanged(sender, line);
        }

        private void HandleDwellStart(ScintillaControl sci, int position, int x, int y)
        {
            if (OnMouseHover == null || sci == null || DisableEvents) return;
            try
            {
                // check mouse over the editor
                if ((position < 0) || simpleTip.Visible || CompletionList.HasMouseIn) return;
                Point mousePos = (PluginBase.MainForm as Form).PointToClient(Cursor.Position);
                if (mousePos.X == lastMousePos.X && mousePos.Y == lastMousePos.Y)
                    return;

                lastMousePos = mousePos;
                Rectangle bounds = GetWindowBounds(sci);
                if (!bounds.Contains(mousePos)) return;

                // check no panel is over the editor
                DockPanel panel = PluginBase.MainForm.DockPanel;
                DockContentCollection panels = panel.Contents;
                foreach (DockContent content in panels)
                {
                    if (content.IsHidden || content.Bounds.Height == 0 || content.Bounds.Width == 0
                        || content.GetType().ToString() == "FlashDevelop.Docking.TabbedDocument") 
                        continue;
                    bounds = GetWindowBounds(content);
                    if (bounds.Contains(mousePos))
                        return;
                }
                if (OnMouseHover != null) OnMouseHover(sci, position);
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError(ex);
                // disable this feature completely
                OnMouseHover = null;
            }
        }

        private Rectangle GetWindowBounds(Control ctrl)
        {
            while (ctrl.Parent != null && !(ctrl is DockWindow)) ctrl = ctrl.Parent;
            if (ctrl != null) return ctrl.Bounds;
            else return new Rectangle();
        }

        private Point GetMousePosIn(Control ctrl)
        {
            Point ctrlPos = ctrl.PointToScreen(new Point());
            Point pos = Cursor.Position;
            return new Point(pos.X - ctrlPos.X, pos.Y - ctrlPos.Y);
        }

        private void HandleDwellEnd(ScintillaControl sci, int position, int x, int y)
        {
            simpleTip.Hide();
            if (OnMouseHoverEnd != null) OnMouseHoverEnd(sci, position);
        }

        #endregion
        
        #region Scintilla Hook
        
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == Win32.WM_MOUSEWHEEL) // capture all MouseWheel events 
            {
                if (!callTip.CallTipActive || !callTip.Focused)
                {
                    if (Win32.ShouldUseWin32())
                    {
                        Win32.SendMessage(CompletionList.GetHandle(), m.Msg, (Int32)m.WParam, (Int32)m.LParam);
                        return true;
                    }
                    else return false;
                }
                else return false;
            }
            else if (m.Msg == Win32.WM_KEYDOWN)
            {
                if ((Keys) m.WParam == Keys.ControlKey) // Ctrl
                {
                    if (CompletionList.Active) CompletionList.FadeOut();
                    if (callTip.CallTipActive && !callTip.Focused) callTip.FadeOut();
                }
            }
            else if (m.Msg == Win32.WM_KEYUP)
            {
                if ((Keys) m.WParam == Keys.ControlKey || (Keys) m.WParam == Keys.Menu) // Ctrl / AltGr
                {
                    if (CompletionList.Active) CompletionList.FadeIn();
                    if (callTip.CallTipActive) callTip.FadeIn();
                }
            }
            return false;
        }
        
        public void LockControl(ScintillaControl sci)
        {
            if (lockedSciControl != null && lockedSciControl.IsAlive && lockedSciControl.Target == sci)
                return;
            UnlockControl();
            //sci.IgnoreAllKeys = true;
            lockedSciControl = new WeakReference(sci);
            Application.AddMessageFilter(this);
        }

        public void UnlockControl()
        {
            if (CompletionList.Active || CallTip.CallTipActive)
                return;
            Application.RemoveMessageFilter(this);
            if (lockedSciControl != null && lockedSciControl.IsAlive)
            {
                ScintillaControl sci = (ScintillaControl)lockedSciControl.Target;
                //sci.IgnoreAllKeys = false;
            }
            lockedSciControl = null;
        }

        private void OnUIRefresh(ScintillaControl sci)
        {
            Form mainForm = PluginBase.MainForm as Form;
            if (mainForm.InvokeRequired)
            {
                mainForm.BeginInvoke((MethodInvoker)delegate { this.OnUIRefresh(sci); });
                return;
            }
            if (sci != null && sci.IsFocus)
            {
                int position = sci.SelectionEnd;
                if (CompletionList.Active && CompletionList.CheckPosition(position)) return;
                if (callTip.CallTipActive && callTip.CheckPosition(position)) return;
            }
            codeTip.Hide();
            callTip.Hide();
            CompletionList.Hide();
            simpleTip.Hide();
        }
        
        private void OnTextInserted(ScintillaControl sci, int position, int length, int linesAdded)
        {
            if (OnTextChanged != null && !DisableEvents) 
                OnTextChanged(sci, position, length, linesAdded);
        }
        private void OnTextDeleted(ScintillaControl sci, int position, int length, int linesAdded)
        {
            if (OnTextChanged != null && !DisableEvents) 
                OnTextChanged(sci, position, -length, linesAdded);
        }

        private void OnChar(ScintillaControl sci, int value)
        {
            if (sci == null || DisableEvents) return;
            if (!CompletionList.Active && !callTip.CallTipActive)
            {
                SendChar(sci, value);
                return;
            }
            if (lockedSciControl != null && lockedSciControl.IsAlive) sci = (ScintillaControl)lockedSciControl.Target;
            else
            {
                codeTip.Hide();
                callTip.Hide();
                CompletionList.Hide();
                SendChar(sci, value);
                return;
            }
            
            if (callTip.CallTipActive) callTip.OnChar(sci, value);
            if (CompletionList.Active) CompletionList.OnChar(sci, value);
            else SendChar(sci, value);
            return;
        }

        public void SendChar(ScintillaControl sci, int value)
        {
            if (OnCharAdded != null) OnCharAdded(sci, value);   
        }

        private bool HandleShortcut(ShortcutKeyEvent e)
        {
            // UITools is currently broadcasting a shortcut, ignore!
            if (ignoreKeys || DisableEvents)
            {
                return false;
            }

            switch (e.Command)
            {
                case "Completion.ListMembers":
                case "Completion.ParameterInfo":
                    /*if (CompletionList.Active || callTip.CallTipActive)
                    {
                        UnlockControl();
                        CompletionList.Hide();
                        codeTip.Hide();
                        callTip.Hide();
                    }*/

                    // Offer to handle the shortcut
                    ignoreKeys = true;
                    var newEvent = new ShortcutKeyEvent(EventType.ShortcutKey, e.Command, e.ShortcutKeys);
                    EventManager.DispatchEvent(this, newEvent);
                    ignoreKeys = false;
                    if (!newEvent.Handled)
                    {
                        // If not handled - show snippets
                        if (PluginBase.MainForm.CurrentDocument.IsEditable
                            && !PluginBase.MainForm.CurrentDocument.SciControl.IsSelectionRectangle)
                        {
                            PluginBase.MainForm.CallCommand("InsertSnippet", "null");
                        }
                    }
                    return true;

                default:
                    return false;
            }
        }

        private bool HandleKeys(Keys key)
        {
            if (key == Keys.None)
            {
                return false;
            }

            if (key == ToggleShowDetailsKey)
            {
                // Toggle "long-description" for the hover tooltip
                if (simpleTip.Visible && !CompletionList.Active)
                {
                    showDetails = !showDetails;
                    simpleTip.UpdateTip(PluginBase.MainForm.CurrentDocument.SciControl);
                    return true;
                }
            }

            // Are we currently displaying something?
            if (CompletionList.Active || callTip.CallTipActive)
            {
                var sci = (ScintillaControl) lockedSciControl?.Target;

                if (sci != null)
                {
                    switch (key)
                    {
                        case ToggleShowDetailsKey:
                            // Toggle "long-description"
                            showDetails = !showDetails;
                            if (callTip.CallTipActive) callTip.UpdateTip(sci);
                            else CompletionList.UpdateTip(null, null);
                            return true;

                        case Keys.Escape:
                            // Hide if pressing Escape
                            break;

                        case Keys.Control | Keys.C: // Hacky...
                        case Keys.Control | Keys.A:
                            if (callTip.Focused)
                            {
                                return false; // Let text copy in tip
                            }
                            // Hide if pressing Ctrl+Key combination
                            break;

                        default:
                            // Hide if pressing Ctrl+Key combination
                            if ((key & Keys.Control) != 0 && (key & Keys.Modifiers) != (Keys.Control | Keys.Alt))
                            {
                                break;
                            }
                            // Handle special keys
                            return (callTip.CallTipActive && callTip.HandleKeys(sci, key))
                                | (CompletionList.Active && CompletionList.HandleKeys(sci, key));
                    }
                }

                // Hide - reach here with the 'break' statement
                UnlockControl();
                CompletionList.Hide((char) Keys.Escape);
                codeTip.Hide();
                callTip.Hide();
            }

            return false;
        }

        /// <summary>
        /// Compute current editor line height
        /// </summary>
        public int LineHeight(ScintillaControl sci)
        {
            if (sci == null) return 0;
            // evaluate the font size
            Font tempFont = new Font(sci.Font.Name, sci.Font.Size+sci.ZoomLevel);
            Graphics g = sci.CreateGraphics();
            SizeF textSize = g.MeasureString("S", tempFont);
            return (int)Math.Ceiling(textSize.Height);
        }

        #endregion
    }
}
