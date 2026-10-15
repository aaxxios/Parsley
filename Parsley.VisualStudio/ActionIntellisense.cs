#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using RoslynCompletionService = Microsoft.CodeAnalysis.Completion.CompletionService;
using RoslynCompletionTrigger = Microsoft.CodeAnalysis.Completion.CompletionTrigger;
using VsCompletionItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;
using RoslynQuickInfoItem = Microsoft.CodeAnalysis.QuickInfo.QuickInfoItem;
using RoslynQuickInfoService = Microsoft.CodeAnalysis.QuickInfo.QuickInfoService;
using VsQuickInfoItem = Microsoft.VisualStudio.Language.Intellisense.QuickInfoItem;
using VsCompletionTrigger = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionTrigger;

namespace Parsley.VisualStudio
{
    /// <summary>
    /// C# completion and quick info in a grammar's semantic actions, from Visual Studio's own C# workspace: the
    /// code the source generator or the command line tool made from the grammar, with the action's current text
    /// in it. See <see cref="ActionWorkspace"/>. It needs the grammar to have been generated once, with at least
    /// one action, into a project in the solution.
    /// </summary>
    [Export(typeof(ActionService))]
    internal sealed class ActionService
    {
        readonly ActionWorkspace _actions = new ActionWorkspace();

        [Import]
        internal VisualStudioWorkspace Workspace { get; set; } = null!;

        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; } = null!;

        /// <summary>
        /// The action at the point, cheaply, without looking for its document: whether to offer anything there
        /// </summary>
        public static ActionSite? SiteAt(SnapshotPoint point) => ActionSite.At(point.Snapshot.GetText(), point.Position);

        /// <summary>
        /// The action at the point in a C# document, or null when it's not in an action or there's no generated
        /// code for the grammar
        /// </summary>
        public async Task<ActionDocument?> GetAsync(SnapshotPoint point, CancellationToken ct)
        {
            var snapshot = point.Snapshot;
            if (!DocumentFactory.TryGetTextDocument(snapshot.TextBuffer, out var file) || string.IsNullOrEmpty(file.FilePath)) return null;
            var text = snapshot.GetText();
            var site = ActionSite.At(text, point.Position);
            if (null == site) return null;
            try
            {
                return await _actions.GetAsync(Workspace.CurrentSolution, file.FilePath, text, site, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine("Parsley: finding the code for an action failed: " + ex);
                return null;
            }
        }

        // how C# colors the parts of a description: Roslyn's text tags as Visual Studio's C# classifications
        static readonly Dictionary<string, string> s_classifications = new Dictionary<string, string>
        {
            [TextTags.Keyword] = "keyword",
            [TextTags.Class] = "class name",
            [TextTags.Record] = "record class name",
            [TextTags.Struct] = "struct name",
            [TextTags.RecordStruct] = "record struct name",
            [TextTags.Interface] = "interface name",
            [TextTags.Enum] = "enum name",
            [TextTags.Delegate] = "delegate name",
            [TextTags.TypeParameter] = "type parameter name",
            [TextTags.Method] = "method name",
            [TextTags.ExtensionMethod] = "extension method name",
            [TextTags.Property] = "property name",
            [TextTags.Field] = "field name",
            [TextTags.Constant] = "constant name",
            [TextTags.EnumMember] = "enum member name",
            [TextTags.Event] = "event name",
            [TextTags.Local] = "local name",
            [TextTags.Parameter] = "parameter name",
            [TextTags.Namespace] = "namespace name",
            [TextTags.NumericLiteral] = "number",
            [TextTags.StringLiteral] = "string",
            [TextTags.Operator] = "operator",
            [TextTags.Punctuation] = "punctuation",
        };

        public static ClassifiedTextElement Classified(IEnumerable<TaggedText> parts) =>
            new ClassifiedTextElement(parts.Select(p => new ClassifiedTextRun(
                s_classifications.TryGetValue(p.Tag, out var c) ? c : PredefinedClassificationTypeNames.Text, p.Text)));

        // the parts of a description, a line each where it breaks them
        public static ContainerElement Description(IEnumerable<TaggedText> parts)
        {
            var lines = new List<object>();
            var line = new List<TaggedText>();
            foreach (var p in parts)
            {
                if (p.Tag == TextTags.LineBreak)
                {
                    lines.Add(Classified(line));
                    line = new List<TaggedText>();
                }
                else line.Add(p);
            }
            if (0 < line.Count) lines.Add(Classified(line));
            return new ContainerElement(ContainerElementStyle.Stacked, lines);
        }

        static readonly (string Tag, ImageMoniker Moniker)[] s_icons =
        {
            (WellKnownTags.ExtensionMethod, KnownMonikers.ExtensionMethod),
            (WellKnownTags.Method, KnownMonikers.Method),
            (WellKnownTags.Property, KnownMonikers.Property),
            (WellKnownTags.Field, KnownMonikers.Field),
            (WellKnownTags.Constant, KnownMonikers.Constant),
            (WellKnownTags.EnumMember, KnownMonikers.EnumerationItemPublic),
            (WellKnownTags.Event, KnownMonikers.Event),
            (WellKnownTags.Local, KnownMonikers.LocalVariable),
            (WellKnownTags.Parameter, KnownMonikers.LocalVariable),
            (WellKnownTags.Class, KnownMonikers.Class),
            (WellKnownTags.Structure, KnownMonikers.Structure),
            (WellKnownTags.Interface, KnownMonikers.Interface),
            (WellKnownTags.Enum, KnownMonikers.Enumeration),
            (WellKnownTags.Delegate, KnownMonikers.Delegate),
            (WellKnownTags.Namespace, KnownMonikers.Namespace),
            (WellKnownTags.Keyword, KnownMonikers.IntellisenseKeyword),
        };

        public static ImageElement? Icon(ImmutableArray<string> tags)
        {
            foreach (var (tag, moniker) in s_icons)
                if (tags.Contains(tag)) return new ImageElement(new ImageId(moniker.Guid, moniker.Id));
            return null;
        }
    }

    #region Completion
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("Parsley semantic action completion")]
    [ContentType(XbnfContentDefinition.ContentType)]
    internal sealed class ActionCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        [Import]
        internal ActionService Actions { get; set; } = null!;

        public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(typeof(ActionCompletionSource), () => new ActionCompletionSource(Actions));
    }

    internal sealed class ActionCompletionSource : IAsyncCompletionSource
    {
        // where an item's Roslyn item and document are kept, for its description
        static readonly object s_item = new object();
        static readonly object s_document = new object();

        readonly ActionService _actions;

        public ActionCompletionSource(ActionService actions) { _actions = actions; }

        static bool _IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        public CompletionStartData InitializeCompletion(VsCompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            switch (trigger.Reason)
            {
                case CompletionTriggerReason.Insertion:
                    if (!_IsWordChar(trigger.Character) && trigger.Character != '.') return CompletionStartData.DoesNotParticipateInCompletion;
                    break;
                case CompletionTriggerReason.Invoke:
                case CompletionTriggerReason.InvokeAndCommitIfUnique:
                    break;
                default:
                    return CompletionStartData.DoesNotParticipateInCompletion;
            }
            if (null == ActionService.SiteAt(triggerLocation)) return CompletionStartData.DoesNotParticipateInCompletion;
            // the word being typed, which the item replaces
            var snapshot = triggerLocation.Snapshot;
            var start = triggerLocation.Position;
            while (start > 0 && _IsWordChar(snapshot[start - 1])) --start;
            var end = triggerLocation.Position;
            while (end < snapshot.Length && _IsWordChar(snapshot[end])) ++end;
            return new CompletionStartData(CompletionParticipation.ProvidesItems, new SnapshotSpan(snapshot, start, end - start));
        }

        public async Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, VsCompletionTrigger trigger,
            SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan, CancellationToken token)
        {
            var action = await _actions.GetAsync(triggerLocation, token).ConfigureAwait(false);
            if (null == action) return CompletionContext.Empty;
            var document = action.Document;
            var service = RoslynCompletionService.GetService(document);
            if (null == service) return CompletionContext.Empty;
            var roslynTrigger = trigger.Reason == CompletionTriggerReason.Insertion
                ? RoslynCompletionTrigger.CreateInsertionTrigger(trigger.Character)
                : RoslynCompletionTrigger.Invoke;
            Microsoft.CodeAnalysis.Completion.CompletionList list;
            try
            {
                list = await service.GetCompletionsAsync(document, action.ToGenerated(triggerLocation.Position), roslynTrigger, cancellationToken: token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine("Parsley: completion in an action failed: " + ex);
                return CompletionContext.Empty;
            }
            if (null == list || 0 == list.ItemsList.Count) return CompletionContext.Empty;
            var items = ImmutableArray.CreateBuilder<VsCompletionItem>(list.ItemsList.Count);
            foreach (var r in list.ItemsList)
            {
                var item = new VsCompletionItem(
                    r.DisplayTextPrefix + r.DisplayText + r.DisplayTextSuffix,
                    this,
                    ActionService.Icon(r.Tags)!,
                    ImmutableArray<CompletionFilter>.Empty,
                    r.InlineDescription,
                    r.DisplayText,
                    r.SortText,
                    r.FilterText,
                    ImmutableArray<ImageElement>.Empty);
                item.Properties.AddProperty(s_item, r);
                item.Properties.AddProperty(s_document, document);
                items.Add(item);
            }
            return new CompletionContext(items.MoveToImmutable());
        }

        public async Task<object?> GetDescriptionAsync(IAsyncCompletionSession session, VsCompletionItem item, CancellationToken token)
        {
            if (!item.Properties.TryGetProperty(s_item, out RoslynCompletionItem r) || !item.Properties.TryGetProperty(s_document, out Document document)) return null;
            var service = RoslynCompletionService.GetService(document);
            if (null == service) return null;
            try
            {
                var description = await service.GetDescriptionAsync(document, r, token).ConfigureAwait(false);
                return null == description || description.TaggedParts.IsDefaultOrEmpty ? null : ActionService.Description(description.TaggedParts);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// Commits an action's completion on the characters C# would, such as "." and "(", as well as Tab and Enter
    /// </summary>
    [Export(typeof(IAsyncCompletionCommitManagerProvider))]
    [Name("Parsley semantic action commit")]
    [ContentType(XbnfContentDefinition.ContentType)]
    internal sealed class ActionCommitManagerProvider : IAsyncCompletionCommitManagerProvider
    {
        public IAsyncCompletionCommitManager GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(typeof(ActionCommitManager), () => new ActionCommitManager());
    }

    internal sealed class ActionCommitManager : IAsyncCompletionCommitManager
    {
        static readonly ImmutableArray<char> s_commit = ImmutableArray.Create('.', '(', ')', '[', ']', ';', ',', ' ', '<', '>', '=', '+', '-', '*', '/', '%', '&', '|', '!', '?', ':');

        public IEnumerable<char> PotentialCommitCharacters => s_commit;

        public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token) =>
            s_commit.Contains(typedChar) && session.GetComputedItems(token).SelectedItem?.Source is ActionCompletionSource;

        // the editor inserts the item's text itself
        public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, VsCompletionItem item, char typedChar, CancellationToken token) =>
            CommitResult.Unhandled;
    }
    #endregion

    #region Quick info
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("Parsley semantic action quick info")]
    [ContentType(XbnfContentDefinition.ContentType)]
    [Order]
    internal sealed class ActionQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
    {
        [Import]
        internal ActionService Actions { get; set; } = null!;

        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer) =>
            textBuffer.Properties.GetOrCreateSingletonProperty(typeof(ActionQuickInfoSource), () => new ActionQuickInfoSource(textBuffer, Actions));
    }

    internal sealed class ActionQuickInfoSource : IAsyncQuickInfoSource
    {
        readonly ITextBuffer _buffer;
        readonly ActionService _actions;

        public ActionQuickInfoSource(ITextBuffer buffer, ActionService actions) { _buffer = buffer; _actions = actions; }

        public async Task<VsQuickInfoItem?> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            var point = session.GetTriggerPoint(_buffer.CurrentSnapshot);
            if (null == point) return null;
            var action = await _actions.GetAsync(point.Value, cancellationToken).ConfigureAwait(false);
            if (null == action) return null;
            var service = RoslynQuickInfoService.GetService(action.Document);
            if (null == service) return null;
            RoslynQuickInfoItem? info;
            try
            {
                info = await service.GetQuickInfoAsync(action.Document, action.ToGenerated(point.Value.Position), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine("Parsley: quick info in an action failed: " + ex);
                return null;
            }
            if (null == info || info.Sections.IsDefaultOrEmpty) return null;
            var start = action.ToGrammar(info.Span.Start);
            var end = action.ToGrammar(info.Span.End);
            if (start < 0 || end < start) return null;
            var snapshot = point.Value.Snapshot;
            var span = snapshot.CreateTrackingSpan(start, end - start, SpanTrackingMode.EdgeInclusive);
            var sections = info.Sections.Select(s => (object)ActionService.Description(s.TaggedParts)).ToList();
            return new VsQuickInfoItem(span, new ContainerElement(ContainerElementStyle.Stacked | ContainerElementStyle.VerticalPadding, sections));
        }

        public void Dispose() { }
    }
    #endregion
}
