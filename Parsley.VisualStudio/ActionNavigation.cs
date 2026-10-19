#nullable enable
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Parsley.VisualStudio
{
    /// <summary>
    /// Go to definition (F12, and Ctrl+click) for the C# in a grammar's semantic actions, from Visual Studio's own C#
    /// workspace, as completion and quick info are: a method, type, property or anything else the action uses goes
    /// to where it's declared, in the project or, for one from a reference, to its metadata; a local the action
    /// declares goes to its declaration in the grammar. Everything else, such as a rule's name or a symbol
    /// constant, and anything outside an action, goes on to the language server, which knows the grammar's rules.
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType(XbnfContentDefinition.ContentType)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class ActionNavigationListener : IVsTextViewCreationListener
    {
        [Import]
        internal IVsEditorAdaptersFactoryService Adapters { get; set; } = null!;

        [Import]
        internal ActionService Actions { get; set; } = null!;

        [Import]
        internal IUIThreadOperationExecutor Executor { get; set; } = null!;

        [Import]
        internal SVsServiceProvider ServiceProvider { get; set; } = null!;

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            var view = Adapters.GetWpfTextView(textViewAdapter);
            if (null == view) return;
            // a filter added to the view sees its commands before the editor's own handlers, the language client's
            // among them, so it can take F12 in an action and pass it on everywhere else
            var filter = new ActionNavigationFilter(view, Actions, Executor, ServiceProvider);
            if (VSConstants.S_OK == textViewAdapter.AddCommandFilter(filter, out var next)) filter.Next = next;
        }
    }

    internal sealed class ActionNavigationFilter : IOleCommandTarget
    {
        readonly IWpfTextView _view;
        readonly ActionService _actions;
        readonly IUIThreadOperationExecutor _executor;
        readonly System.IServiceProvider _services;

        public ActionNavigationFilter(IWpfTextView view, ActionService actions, IUIThreadOperationExecutor executor, System.IServiceProvider services)
        {
            _view = view; _actions = actions; _executor = executor; _services = services;
        }

        public IOleCommandTarget? Next { get; set; }

        static bool _IsGoToDefinition(Guid group, uint id) =>
            group == VSConstants.GUID_VSStandardCommandSet97 && id == (uint)VSConstants.VSStd97CmdID.GotoDefn;

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var status = null != Next ? Next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) : (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            // the language client offers go to definition everywhere in a grammar; only if it doesn't, offer it in an
            // action, which needs the grammar scanned, so it isn't done each time the caret moves otherwise
            var offered = VSConstants.S_OK == status && 1 == cCmds && 0 != (prgCmds[0].cmdf & (uint)OLECMDF.OLECMDF_ENABLED);
            if (!offered && 1 == cCmds && _IsGoToDefinition(pguidCmdGroup, prgCmds[0].cmdID) && null != _CaretPoint())
            {
                prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                return VSConstants.S_OK;
            }
            return status;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_IsGoToDefinition(pguidCmdGroup, nCmdID) && _TryGoToDefinition()) return VSConstants.S_OK;
            return null != Next ? Next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) : (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        // the caret, in the grammar's buffer, when it's in an action
        SnapshotPoint? _CaretPoint()
        {
            var point = _view.Caret.Position.Point.GetPoint(b => b.ContentType.IsOfType(XbnfContentDefinition.ContentType), PositionAffinity.Successor);
            return null != point && null != ActionService.SiteAt(point.Value) ? point : null;
        }

        // true when it navigated; false leaves the command to whatever comes next
        bool _TryGoToDefinition()
        {
            var point = _CaretPoint();
            if (null == point) return false;
            var handled = false;
            try
            {
                // the first time, finding the code generated from the grammar can take a moment
                _executor.Execute("Go To Definition", "Finding the definition…", allowCancellation: true, showProgress: false, ctx =>
                {
                    handled = ThreadHelper.JoinableTaskFactory.Run(() => _GoToDefinitionAsync(point.Value, ctx.UserCancellationToken));
                });
            }
            catch (OperationCanceledException) { return true; }
            catch (Exception ex)
            {
                Debug.WriteLine("Parsley: go to definition in an action failed: " + ex);
                return false;
            }
            return handled;
        }

        async Task<bool> _GoToDefinitionAsync(SnapshotPoint point, CancellationToken ct)
        {
            await TaskScheduler.Default;
            var action = await _actions.GetAsync(point, ct).ConfigureAwait(false);
            if (null == action) return false;
            var document = action.Document;
            var position = action.ToGenerated(point.Position);
            var workspace = _actions.Workspace;
            // at the end of a name, the name
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, ct).ConfigureAwait(false);
            if (null == symbol && position > 0)
                symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position - 1, ct).ConfigureAwait(false);
            if (null == symbol) return false;
            // a call to an extension method or a generic method: the method as declared
            symbol = symbol is IMethodSymbol m ? (m.ReducedFrom ?? m).OriginalDefinition : symbol.OriginalDefinition;

            var solution = document.Project.Solution;
            var sources = symbol.Locations.Where(l => l.IsInSource && null != l.SourceTree).ToList();
            if (0 == sources.Count)
            {
                // from a reference: its metadata, as C# shows it
                return await workspace.TryGoToDefinitionAsync(symbol, document.Project, ct).ConfigureAwait(true);
            }
            foreach (var location in sources)
            {
                var at = solution.GetDocument(location.SourceTree);
                if (null == at) continue;
                if (at.Id == document.Id)
                {
                    // declared in the action: a local, which is in the grammar. Anything else the generated code
                    // declares, such as a symbol constant, is for the language server, which maps it to its rule.
                    var offset = action.ToGrammar(location.SourceSpan.Start);
                    if (offset < 0) continue;
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                    _MoveCaret(point.Snapshot, offset, location.SourceSpan.Length);
                    return true;
                }
                // a file of the project's own, not one only the copy of it has, such as the generator's output
                var live = workspace.CurrentSolution.GetDocument(at.Id);
                var path = live?.FilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                var line = location.GetLineSpan().StartLinePosition;
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                if (_Open(path!, line.Line, line.Character)) return true;
            }
            return false;
        }

        void _MoveCaret(ITextSnapshot snapshot, int offset, int length)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var current = snapshot.TextBuffer.CurrentSnapshot;
            var span = new SnapshotSpan(snapshot, offset, length).TranslateTo(current, SpanTrackingMode.EdgeExclusive);
            var target = _view.BufferGraph.MapUpToSnapshot(span.Start, PointTrackingMode.Positive, PositionAffinity.Successor, _view.TextSnapshot);
            if (null == target) return;
            _view.Caret.MoveTo(target.Value);
            _view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(target.Value, span.Length), EnsureSpanVisibleOptions.AlwaysCenter);
        }

        bool _Open(string path, int line, int column)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.OpenDocument(_services, path, VSConstants.LOGVIEWID_TextView, out _, out _, out var frame, out var view);
            if (null == view) return false;
            frame?.Show();
            view.SetCaretPos(line, column);
            view.CenterLines(line, 1);
            return true;
        }
    }
}
