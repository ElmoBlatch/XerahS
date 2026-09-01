#region License Information (GPL v3)

/*
    XerahS - The Avalonia UI implementation of ShareX
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using NUnit.Framework;
using ShareX.ImageEditor.Core.Annotations;
using ShareX.ImageEditor.Hosting;
using SkiaSharp;
using XerahS.Core;
using XerahS.Core.Tasks.Processors;
using XerahS.Platform.Abstractions;

namespace XerahS.Tests.Tasks;

[TestFixture]
public sealed class CaptureJobProcessorEditorCancelTests
{
    [SetUp]
    public void SetUp() => PlatformServices.Reset();

    [TearDown]
    public void TearDown() => PlatformServices.Reset();

    [Test]
    public async Task ProcessAsync_AnnotateMediaCancelled_AbortsBeforeDownstreamJobs()
    {
        // A null editor session result means the user cancelled/closed the editor (Esc, Cancel button,
        // or the window close button) — they did NOT choose "Continue".
        var ui = new StubEditorUiService(editorResult: null);
        PlatformServices.RegisterUIService(ui);

        var input = new SKBitmap(8, 8);
        var settings = new TaskSettings();
        settings.AfterCaptureJob = AfterCaptureTasks.AnnotateMedia;

        var info = new TaskInfo(settings) { Metadata = new TaskMetadata(input) };

        bool shouldContinue = await new CaptureJobProcessor().ProcessAsync(info, CancellationToken.None);

        // Returning false is what makes FinalizationStage mark the task Canceled and stop the pipeline
        // before save / clipboard / upload run — i.e. the capture is discarded.
        Assert.That(ui.EditorShown, Is.True);
        Assert.That(shouldContinue, Is.False);
    }

    [Test]
    public async Task ProcessAsync_AnnotateMediaContinued_KeepsRenderedImage_AndContinues()
    {
        var rendered = new SKBitmap(8, 8);
        var ui = new StubEditorUiService(
            new ImageEditorSessionResult(rendered, sourceImage: null, Array.Empty<Annotation>()));
        PlatformServices.RegisterUIService(ui);

        // Not wrapped in `using`: the processor disposes the original input bitmap when it swaps in
        // the rendered image.
        var input = new SKBitmap(8, 8);
        var settings = new TaskSettings();
        settings.AfterCaptureJob = AfterCaptureTasks.AnnotateMedia;

        var info = new TaskInfo(settings) { Metadata = new TaskMetadata(input) };

        bool shouldContinue = await new CaptureJobProcessor().ProcessAsync(info, CancellationToken.None);

        Assert.That(shouldContinue, Is.True);
        Assert.That(info.Metadata!.Image, Is.SameAs(rendered));

        rendered.Dispose();
    }

    private sealed class StubEditorUiService : IUIService
    {
        private readonly ImageEditorSessionResult? _editorResult;

        public StubEditorUiService(ImageEditorSessionResult? editorResult) => _editorResult = editorResult;

        public bool EditorShown { get; private set; }

        public Task<ImageEditorSessionResult?> ShowEditorSessionAsync(
            SKBitmap image,
            string? sourceFilePath = null,
            bool taskMode = false,
            IReadOnlyList<Annotation>? annotations = null,
            bool restoredAnnotations = false)
        {
            EditorShown = true;
            return Task.FromResult(_editorResult);
        }

        public Task<SKBitmap?> ShowEditorAsync(SKBitmap image, string? sourceFilePath = null, bool taskMode = false)
            => Task.FromResult<SKBitmap?>(null);

        public Task HideMainWindowAsync() => Task.CompletedTask;

        public Task RestoreMainWindowAsync() => Task.CompletedTask;

        public Task<string?> ShowVideoEditorAsync(string videoPath, string? ffmpegPath) => Task.FromResult<string?>(null);

        public Task<(AfterCaptureTasks Capture, AfterUploadTasks Upload, bool Cancel, AfterCaptureQuickAction QuickAction)> ShowAfterCaptureWindowAsync(
            SKBitmap image,
            AfterCaptureTasks afterCapture,
            AfterUploadTasks afterUpload) => Task.FromResult((afterCapture, afterUpload, false, AfterCaptureQuickAction.None));

        public Task ShowAfterUploadWindowAsync(AfterUploadWindowInfo info) => Task.CompletedTask;

        public Task<SendToPromptResult> ShowSendToPromptAsync(SendToSelection selection) => Task.FromResult(new SendToPromptResult());

        public Task ExecuteSendToActionAsync(SendToAction action, SendToSelection selection, SendToPromptResult? decision = null) => Task.CompletedTask;

        public Task ShowOcrWindowAsync(SKBitmap image) => Task.CompletedTask;

        public Task ShowAnalyzerWindowAsync(SKBitmap image) => Task.CompletedTask;
    }
}
