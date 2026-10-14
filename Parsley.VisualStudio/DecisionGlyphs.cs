using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Parsley.VisualStudio
{
    /// <summary>
    /// The glyphs' colors, which you can change in Tools > Options > Environment > Fonts and Colors, under Text Editor
    /// </summary>
    internal static class DecisionGlyphFormats
    {
        public const string LLk = "XBNF Lookahead LL(k)";
        public const string AllStar = "XBNF Lookahead ALL(*)";
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name(DecisionGlyphFormats.LLk)]
    [UserVisible(true)]
    internal sealed class LLkGlyphFormat : EditorFormatDefinition
    {
        public LLkGlyphFormat()
        {
            DisplayName = "XBNF Lookahead Glyph: LL(k)";
            ForegroundColor = Color.FromRgb(0x9B, 0x6B, 0xD6);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name(DecisionGlyphFormats.AllStar)]
    [UserVisible(true)]
    internal sealed class AllStarGlyphFormat : EditorFormatDefinition
    {
        public AllStarGlyphFormat()
        {
            DisplayName = "XBNF Lookahead Glyph: ALL(*)";
            ForegroundColor = Color.FromRgb(0xE0, 0x86, 0x1A);
        }
    }

    /// <summary>
    /// Makes a glyph tagger for each .xbnf editor
    /// </summary>
    [Export(typeof(IViewTaggerProvider))]
    [ContentType(XbnfContentDefinition.ContentType)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TagType(typeof(IntraTextAdornmentTag))]
    internal sealed class DecisionGlyphTaggerProvider : IViewTaggerProvider
    {
        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; }

        [Import]
        internal IEditorFormatMapService FormatMapService { get; set; }

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            // only the view's own buffer, not ones projected into it
            if (!(textView is IWpfTextView view) || textView.TextBuffer != buffer) return null;
            if (!DocumentFactory.TryGetTextDocument(buffer, out var document)) return null;
            return view.Properties.GetOrCreateSingletonProperty(typeof(DecisionGlyphTagger),
                () => new DecisionGlyphTagger(view, document, FormatMapService.GetEditorFormatMap(view))) as ITagger<T>;
        }
    }

    /// <summary>
    /// A glyph in the text at each decision that needs more than one token of lookahead: ◇ with k for LL(k),
    /// ◆ for ALL(*), with the server's explanation as its tooltip. It fetches them when the server says they've
    /// changed, after an analysis, and shortly after each edit, which can hide the ones in an edited rule.
    /// In between, they move with the text.
    /// </summary>
    internal sealed class DecisionGlyphTagger : ITagger<IntraTextAdornmentTag>
    {
        const int EditDelayMs = 500;

        sealed class Glyph
        {
            public ITrackingPoint Point;
            public DecisionHintInfo Hint;
            public UIElement Element;
        }

        readonly IWpfTextView _view;
        readonly ITextDocument _document;
        readonly IEditorFormatMap _formats;
        readonly DispatcherTimer _editTimer;
        // the fetches run in a collection of the tagger's own rather than through ThreadHelper's factory, which
        // VS doesn't wait for (VSSDK007); there's no package here to own them
        readonly JoinableTaskCollection _tasks;
        readonly JoinableTaskFactory _jtf;
        List<Glyph> _glyphs = new List<Glyph>();
        CancellationTokenSource _fetch;
        bool _closed;

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public DecisionGlyphTagger(IWpfTextView view, ITextDocument document, IEditorFormatMap formats)
        {
            _view = view;
            _document = document;
            _formats = formats;
            _tasks = ThreadHelper.JoinableTaskContext.CreateCollection();
            _jtf = ThreadHelper.JoinableTaskContext.CreateFactory(_tasks);
            _editTimer = new DispatcherTimer(DispatcherPriority.Background, view.VisualElement.Dispatcher) { Interval = TimeSpan.FromMilliseconds(EditDelayMs) };
            _editTimer.Tick += (s, e) => { _editTimer.Stop(); _Fetch(); };
            _view.TextBuffer.Changed += _OnBufferChanged;
            _formats.FormatMappingChanged += _OnFormatsChanged;
            DecisionHintService.Changed += _OnServerChanged;
            _view.Closed += _OnClosed;
            _Fetch();
        }

        void _OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            _editTimer.Stop();
            _editTimer.Start();
        }

        void _OnServerChanged(string path)
        {
            if (null == path || DecisionHintService.SamePath(path, _document.FilePath)) _Fetch();
        }

        void _OnFormatsChanged(object sender, FormatItemsEventArgs e)
        {
            // new elements in the new colors
            foreach (var g in _glyphs) g.Element = null;
            _RaiseAll();
        }

        void _OnClosed(object sender, EventArgs e)
        {
            _closed = true;
            _editTimer.Stop();
            _fetch?.Cancel();
            _view.TextBuffer.Changed -= _OnBufferChanged;
            _formats.FormatMappingChanged -= _OnFormatsChanged;
            DecisionHintService.Changed -= _OnServerChanged;
            _view.Closed -= _OnClosed;
        }

        // asks the server for the hints, replacing any fetch still waiting
        void _Fetch()
        {
            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _fetch, cts)?.Cancel();
            _jtf.RunAsync(() => _FetchAsync(cts.Token)).Task.Forget();
        }

        async Task _FetchAsync(CancellationToken ct)
        {
            IReadOnlyList<DecisionHintInfo> hints;
            try
            {
                hints = await DecisionHintService.GetAsync(_document.FilePath, ct);
                await _jtf.SwitchToMainThreadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // a newer fetch replaced this one, or the view closed
                return;
            }
            if (_closed || ct.IsCancellationRequested) return;
            var snapshot = _view.TextBuffer.CurrentSnapshot;
            var glyphs = new List<Glyph>();
            foreach (var h in hints)
            {
                if (h.Line < 0 || h.Line >= snapshot.LineCount) continue;
                var line = snapshot.GetLineFromLineNumber(h.Line);
                if (h.Character < 0 || h.Character > line.Length) continue;
                // positive: text typed right at the glyph goes before it, so it stays with the token after it
                glyphs.Add(new Glyph { Point = snapshot.CreateTrackingPoint(line.Start.Position + h.Character, PointTrackingMode.Positive), Hint = h });
            }
            _glyphs = glyphs;
            _RaiseAll();
        }

        void _RaiseAll()
        {
            var snapshot = _view.TextBuffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (0 == spans.Count || 0 == _glyphs.Count) yield break;
            var snapshot = spans[0].Snapshot;
            foreach (var g in _glyphs)
            {
                var at = g.Point.GetPoint(snapshot);
                if (!spans.Any(s => s.Start <= at && at <= s.End)) continue;
                g.Element = g.Element ?? _Create(g.Hint);
                yield return new TagSpan<IntraTextAdornmentTag>(new SnapshotSpan(at, 0),
                    new IntraTextAdornmentTag(g.Element, null, PositionAffinity.Successor));
            }
        }

        UIElement _Create(DecisionHintInfo hint)
        {
            var text = new TextBlock
            {
                Text = hint.Label,
                ToolTip = hint.Tooltip,
                Foreground = _Brush(hint.AllStar ? DecisionGlyphFormats.AllStar : DecisionGlyphFormats.LLk),
                // so the tooltip shows anywhere over it, not only over the glyph's strokes
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var props = _view.FormattedLineSource?.DefaultTextProperties;
            if (null != props)
            {
                text.FontFamily = props.Typeface.FontFamily;
                text.FontSize = props.FontRenderingEmSize;
            }
            ToolTipService.SetInitialShowDelay(text, 250);
            return text;
        }

        Brush _Brush(string format)
        {
            var props = _formats.GetProperties(format);
            if (props[EditorFormatDefinition.ForegroundBrushId] is Brush brush) return brush;
            if (props[EditorFormatDefinition.ForegroundColorId] is Color color) return new SolidColorBrush(color);
            return SystemColors.GrayTextBrush;
        }
    }
}