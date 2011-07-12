﻿using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Moq;
using NUnit.Framework;
using Vim;
using Vim.Extensions;
using Vim.UnitTest;
using Vim.UnitTest.Mock;

namespace VsVim.UnitTest
{
    [TestFixture]
    public sealed class VsCommandTargetTest : VimTestBase
    {
        private MockRepository _factory;
        private IVimBuffer _buffer;
        private IVim _vim;
        private ITextView _textView;
        private Mock<IVsAdapter> _adapter;
        private Mock<IExternalEditorManager> _externalEditorManager;
        private Mock<IOleCommandTarget> _nextTarget;
        private Mock<IDisplayWindowBroker> _broker;
        private VsCommandTarget _targetRaw;
        private IOleCommandTarget _target;

        [SetUp]
        public void SetUp()
        {
            _textView = EditorUtil.CreateTextView("");
            _buffer = EditorUtil.FactoryService.Vim.CreateBuffer(_textView);
            _vim = _buffer.Vim;
            _factory = new MockRepository(MockBehavior.Strict);

            // By default Resharper isn't loaded
            _externalEditorManager = _factory.Create<IExternalEditorManager>();
            _externalEditorManager.SetupGet(x => x.IsResharperInstalled).Returns(false);

            _nextTarget = _factory.Create<IOleCommandTarget>(MockBehavior.Loose);
            _adapter = _factory.Create<IVsAdapter>();
            _adapter.Setup(x => x.InAutomationFunction).Returns(false);
            _adapter.Setup(x => x.InDebugMode).Returns(false);
            _adapter.Setup(x => x.IsIncrementalSearchActive(It.IsAny<ITextView>())).Returns(false);

            _broker = _factory.Create<IDisplayWindowBroker>(MockBehavior.Loose);

            var oldCommandFilter = _nextTarget.Object;
            var vsTextView = _factory.Create<IVsTextView>(MockBehavior.Loose);
            vsTextView.Setup(x => x.AddCommandFilter(It.IsAny<IOleCommandTarget>(), out oldCommandFilter)).Returns(0);
            var result = VsCommandTarget.Create(
                _buffer,
                vsTextView.Object,
                _adapter.Object,
                _broker.Object,
                _externalEditorManager.Object);
            Assert.IsTrue(result.IsSuccess);
            _targetRaw = result.Value;
            _target = _targetRaw;
        }

        /// <summary>
        /// Make sure to clear the KeyMap map on tear down so we don't mess up other tests
        /// </summary>
        [TearDown]
        public void TearDown()
        {
            _vim.KeyMap.ClearAll();
        }

        /// <summary>
        /// Run the KeyInput value through Exec
        /// </summary>
        private void RunExec(KeyInput keyInput)
        {
            OleCommandData data;
            Guid commandGroup;
            Assert.IsTrue(OleCommandUtil.TryConvert(keyInput, out commandGroup, out data));
            try
            {
                _target.Exec(ref commandGroup, data.CommandId, data.CommandExecOpt, data.VariantIn, data.VariantOut);
            }
            finally
            {
                OleCommandData.Release(ref data);
            }
        }

        /// <summary>
        /// Run the given command as a type char through the Exec function
        /// </summary>
        private void RunExec(char c)
        {
            var keyInput = KeyInputUtil.CharToKeyInput(c);
            RunExec(keyInput);
        }

        /// <summary>
        /// Run the KeyInput value through QueryStatus.  Returns true if the QueryStatus call
        /// indicated the command was supported
        /// </summary>
        private bool RunQueryStatus(KeyInput keyInput)
        {
            OleCommandData data;
            Guid commandGroup;
            Assert.IsTrue(OleCommandUtil.TryConvert(keyInput, out commandGroup, out data));
            try
            {
                var cmds = new OLECMD[1];
                cmds[0] = new OLECMD { cmdID = data.CommandId};
                return
                    ErrorHandler.Succeeded(_target.QueryStatus(ref commandGroup, 1, cmds, data.VariantIn)) &&
                    cmds[0].cmdf == (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
            }
            finally
            {
                OleCommandData.Release(ref data);
            }
        }

        /// <summary>
        /// Run the char through the QueryStatus method
        /// </summary>
        private bool RunQueryStatus(char c)
        {
            var keyInput = KeyInputUtil.CharToKeyInput(c);
            return RunQueryStatus(keyInput);
        }

        private void AssertCannotConvert2K(VSConstants.VSStd2KCmdID id)
        {
            KeyInput ki;
            Assert.IsFalse(_targetRaw.TryConvert(VSConstants.VSStd2K, (uint)id, IntPtr.Zero, out ki));
        }

        private void AssertCanConvert2K(VSConstants.VSStd2KCmdID id, KeyInput expected)
        {
            KeyInput ki;
            Assert.IsTrue(_targetRaw.TryConvert(VSConstants.VSStd2K, (uint)id, IntPtr.Zero, out ki));
            Assert.AreEqual(expected, ki);
        }

        [Test]
        public void TryConvert_Tab()
        {
            AssertCanConvert2K(VSConstants.VSStd2KCmdID.TAB, KeyInputUtil.TabKey);
        }

        [Test]
        public void TryConvert_InAutomationShouldFail()
        {
            _adapter.Setup(x => x.InAutomationFunction).Returns(true);
            AssertCannotConvert2K(VSConstants.VSStd2KCmdID.TAB);
        }

        [Test]
        public void TryConvert_InIncrementalSearchShouldFail()
        {
            _adapter.Setup(x => x.IsIncrementalSearchActive(It.IsAny<ITextView>())).Returns(true);
            AssertCannotConvert2K(VSConstants.VSStd2KCmdID.TAB);
        }

        [Test]
        public void QueryStatus_IgnoreEscapeIfCantProcess()
        {
            _buffer.SwitchMode(ModeKind.Disabled, ModeArgument.None);
            Assert.IsFalse(_buffer.CanProcess(KeyInputUtil.EscapeKey));
            _nextTarget.SetupQueryStatus().Verifiable();
            RunQueryStatus(KeyInputUtil.EscapeKey);
            _factory.Verify();
        }

        [Test]
        public void QueryStatus_EnableEscapeButDontHandleNormally()
        {
            _buffer.SwitchMode(ModeKind.Insert, ModeArgument.None);
            Assert.IsTrue(_buffer.CanProcess(VimKey.Escape));
            Assert.IsTrue(RunQueryStatus(KeyInputUtil.EscapeKey));
        }

        /// <summary>
        /// Don't actually run the Escape in the QueryStatus command if we're in visual mode
        /// </summary>
        [Test]
        public void QueryStatus_EnableEscapeAndDontHandleInResharperPlusVisualMode()
        {
            var count = 0;
            _buffer.KeyInputProcessed += delegate { count++; };
            _buffer.SwitchMode(ModeKind.VisualCharacter, ModeArgument.None);
            _externalEditorManager.SetupGet(x => x.IsResharperInstalled).Returns(true).Verifiable();
            RunQueryStatus(KeyInputUtil.EscapeKey);
            Assert.AreEqual(0, count);
            _factory.Verify();
        }

        /// <summary>
        /// Make sure we process Escape during QueryStatus if we're in insert mode.  R# will
        /// intercept escape and never give it to us and we'll think we're still in insert
        /// </summary>
        [Test]
        public void QueryStatus_EnableAndHandleEscapeInResharperPlusInsert()
        {
            var count = 0;
            _buffer.KeyInputProcessed += delegate { count++; };
            _buffer.SwitchMode(ModeKind.Insert, ModeArgument.None);
            _externalEditorManager.SetupGet(x => x.IsResharperInstalled).Returns(true).Verifiable();
            Assert.IsTrue(RunQueryStatus(KeyInputUtil.EscapeKey));
            Assert.IsTrue(_targetRaw.SwallowIfNextExecMatches.IsSome());
            Assert.AreEqual(KeyInputUtil.EscapeKey, _targetRaw.SwallowIfNextExecMatches.Value);
            Assert.AreEqual(1, count);
            _factory.Verify();
        }

        /// <summary>
        /// Make sure we process Escape during QueryStatus if we're in insert mode.  R# will
        /// intercept escape and never give it to us and we'll think we're still in insert
        /// </summary>
        [Test]
        public void QueryStatus_EnableAndHandleEscapeInResharperPlusExternalEdit()
        {
            var count = 0;
            _buffer.KeyInputProcessed += delegate { count++; };
            _buffer.SwitchMode(ModeKind.ExternalEdit, ModeArgument.None);
            _externalEditorManager.SetupGet(x => x.IsResharperInstalled).Returns(true).Verifiable();
            Assert.IsTrue(RunQueryStatus(KeyInputUtil.EscapeKey));
            Assert.IsTrue(_targetRaw.SwallowIfNextExecMatches.IsSome());
            Assert.AreEqual(KeyInputUtil.EscapeKey, _targetRaw.SwallowIfNextExecMatches.Value);
            Assert.AreEqual(1, count);
            _factory.Verify();
        }

        /// <summary>
        /// The Backspace key isn't special so don't special case it in R#
        /// </summary>
        [Test]
        public void QueryStatus_HandleBackspaceNormallyInResharperMode()
        {
            var count = 0;
            _buffer.KeyInputProcessed += delegate { count++; };
            _buffer.SwitchMode(ModeKind.Normal, ModeArgument.None);
            _externalEditorManager.SetupGet(x => x.IsResharperInstalled).Returns(true).Verifiable();
            Assert.IsTrue(RunQueryStatus(KeyInputUtil.VimKeyToKeyInput(VimKey.Back)));
            Assert.AreEqual(0, count);
            _factory.Verify();
        }

        /// <summary>
        /// When Visual Studio is in debug mode R# will attempt to handle the Enter key directly
        /// and do nothing.  Presumably they are doing this because it is an edit command and they
        /// are suppressing it's action.  We want to process this directly though if Vim believes
        /// Enter to be a command and not an edit, for example in normal mode
        /// </summary>
        [Test]
        public void QueryStatus_Resharper_HandleEnterInDebugModeIfCommand()
        {
            _textView.SetText("cat", "dog");
            _textView.MoveCaretTo(0);
            _externalEditorManager.SetupGet(x => x.IsResharperInstalled).Returns(true).Verifiable();
            _adapter.SetupGet(x => x.InDebugMode).Returns(true).Verifiable();
            _buffer.SwitchMode(ModeKind.Normal, ModeArgument.None);
            Assert.IsTrue(_buffer.CanProcessAsCommand(KeyInputUtil.EnterKey));
            Assert.IsTrue(RunQueryStatus(KeyInputUtil.EnterKey));
            Assert.AreEqual(_textView.GetLine(1).Start, _textView.GetCaretPoint());
            Assert.AreEqual(KeyInputUtil.EnterKey, _targetRaw.SwallowIfNextExecMatches.Value);
            _factory.Verify();
        }

        /// <summary>
        /// If Enter isn't going to be processed as a command then don't special case it in Debug
        /// mode for R#.  It would be an edit and we don't want to interfere with R#'s handling 
        /// of edits
        /// </summary>
        [Test]
        public void QueryStatus_Resharper_DontHandleEnterInDebugModeIfNotCommand()
        {
            _textView.SetText("cat", "dog");
            _textView.MoveCaretTo(0);
            var savedSnapshot = _textView.TextSnapshot;
            _externalEditorManager.SetupGet(x => x.IsResharperInstalled).Returns(true).Verifiable();
            _adapter.SetupGet(x => x.InDebugMode).Returns(true).Verifiable();
            _buffer.SwitchMode(ModeKind.Insert, ModeArgument.None);
            Assert.IsFalse(_buffer.CanProcessAsCommand(KeyInputUtil.EnterKey));
            Assert.IsTrue(RunQueryStatus(KeyInputUtil.EnterKey));
            Assert.IsTrue(_targetRaw.SwallowIfNextExecMatches.IsNone());
            Assert.AreEqual(_textView.GetLine(0).Start, _textView.GetCaretPoint());
            Assert.AreSame(savedSnapshot, _textView.TextSnapshot);
            _factory.Verify();
        }

        [Test]
        public void Exec_PassOnIfCantHandle()
        {
            _buffer.SwitchMode(ModeKind.Disabled, ModeArgument.None);
            Assert.IsFalse(_buffer.CanProcess(VimKey.Enter));
            _nextTarget.SetupExec().Verifiable();
            RunExec(KeyInputUtil.EnterKey);
            _factory.Verify();
        }

        [Test]
        public void Exec_SwallowShouldNotPassOnTheCommandIfMatches()
        {
            _targetRaw.SwallowIfNextExecMatches = FSharpOption.Create(KeyInputUtil.EscapeKey);
            RunExec(KeyInputUtil.EscapeKey);
            Assert.IsTrue(_targetRaw.SwallowIfNextExecMatches.IsNone());
            _factory.Verify();
        }

        [Test]
        public void Exec_HandleEscapeNormally()
        {
            var count = 0;
            _buffer.KeyInputProcessed += delegate { count++; };
            _buffer.SwitchMode(ModeKind.Insert, ModeArgument.None);
            RunExec(KeyInputUtil.EscapeKey);
            Assert.AreEqual(1, count);
        }

        /// <summary>
        /// Make sure that KeyInput is simulated for any KeyInput which is intercepted
        /// </summary>
        [Test]
        public void Exec_SimulateInterceptedInput()
        {
            var count = 0;
            _buffer.KeyInputProcessed += delegate { count++; };
            _buffer.SwitchMode(ModeKind.Insert, ModeArgument.None);
            _nextTarget.SetupExec().Verifiable();
            RunExec(KeyInputUtil.EnterKey);
            Assert.AreEqual(1, count);
            _factory.Verify();
        }

        /// <summary>
        /// If there is buffered KeyInput values then the provided KeyInput shouldn't ever be 
        /// directly handled by the VsCommandTarget or the next IOleCommandTarget in the 
        /// chain.  It should be passed directly to the IVimBuffer if it can be handled else 
        /// it shouldn't be handled
        /// </summary>
        [Test]
        public void Exec_WithUnmatchedBufferedInput()
        {
            _vim.KeyMap.MapWithNoRemap("jj", "hello", KeyRemapMode.Insert);
            _buffer.SwitchMode(ModeKind.Insert, ModeArgument.None);
            RunExec('j');
            Assert.IsFalse(_buffer.BufferedRemapKeyInputs.IsEmpty);
            RunExec('a');
            Assert.AreEqual("ja", _textView.GetLine(0).GetText());
            Assert.IsTrue(_buffer.BufferedRemapKeyInputs.IsEmpty);
        }

        /// <summary>
        /// Make sure in the case that the next input matches the final expansion of a 
        /// buffered input that it's processed correctly
        /// </summary>
        [Test]
        public void Exec_WithMatchedBufferedInput()
        {
            _vim.KeyMap.MapWithNoRemap("jj", "hello", KeyRemapMode.Insert);
            _buffer.SwitchMode(ModeKind.Insert, ModeArgument.None);
            RunExec('j');
            Assert.IsFalse(_buffer.BufferedRemapKeyInputs.IsEmpty);
            RunExec('j');
            Assert.AreEqual("hello", _textView.GetLine(0).GetText());
            Assert.IsTrue(_buffer.BufferedRemapKeyInputs.IsEmpty);
        }

        /// <summary>
        /// In the case where there is buffered KeyInput values and the next KeyInput collapses
        /// it into a single value then we should process the result as a single key stroke and
        /// go through Exec
        /// </summary>
        [Test]
        public void Exec_CollapseBufferedInputToSingleKeyInput()
        {
            _vim.KeyMap.MapWithNoRemap("jj", "z", KeyRemapMode.Insert);
            _buffer.SwitchMode(ModeKind.Insert, ModeArgument.None);
            RunExec('j');
            Assert.IsFalse(_buffer.BufferedRemapKeyInputs.IsEmpty);
            _nextTarget.SetupExec().Callback(() => _textView.SetText("hello")).Verifiable();
            RunExec('j');
            Assert.AreEqual("hello", _textView.GetLine(0).GetText());
            Assert.IsTrue(_buffer.BufferedRemapKeyInputs.IsEmpty);
            _nextTarget.Verify();
        }

    }
}

