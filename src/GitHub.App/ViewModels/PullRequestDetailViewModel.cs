﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using GitHub.App;
using GitHub.Exports;
using GitHub.Extensions;
using GitHub.Models;
using GitHub.Services;
using GitHub.Settings;
using GitHub.UI;
using LibGit2Sharp;
using NullGuard;
using ReactiveUI;

namespace GitHub.ViewModels
{
    /// <summary>
    /// A view model which displays the details of a pull request.
    /// </summary>
    [ExportViewModel(ViewType = UIViewType.PRDetail)]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    [NullGuard(ValidationFlags.None)]
    public class PullRequestDetailViewModel : BaseViewModel, IPullRequestDetailViewModel
    {
        readonly ILocalRepositoryModel repository;
        readonly IModelService modelService;
        readonly IPullRequestService pullRequestsService;
        IPullRequestModel model;
        string sourceBranchDisplayName;
        string targetBranchDisplayName;
        string body;
        ChangedFilesViewType changedFilesViewType;
        OpenChangedFileAction openChangedFileAction;
        IPullRequestCheckoutState checkoutState;
        IPullRequestUpdateState updateState;
        string operationError;

        /// <summary>
        /// Initializes a new instance of the <see cref="PullRequestDetailViewModel"/> class.
        /// </summary>
        /// <param name="connectionRepositoryHostMap">The connection repository host map.</param>
        /// <param name="teservice">The team explorer service.</param>
        /// <param name="pullRequestsService">The pull requests service.</param>
        /// <param name="avatarProvider">The avatar provider.</param>
        [ImportingConstructor]
        PullRequestDetailViewModel(
            IConnectionRepositoryHostMap connectionRepositoryHostMap,
            ITeamExplorerServiceHolder teservice,
            IPullRequestService pullRequestsService,
            IPackageSettings settings)
            : this(teservice.ActiveRepo,
                  connectionRepositoryHostMap.CurrentRepositoryHost.ModelService,
                  pullRequestsService,
                  settings)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PullRequestDetailViewModel"/> class.
        /// </summary>
        /// <param name="repositoryHost">The repository host.</param>
        /// <param name="teservice">The team explorer service.</param>
        /// <param name="pullRequestsService">The pull requests service.</param>
        /// <param name="avatarProvider">The avatar provider.</param>
        public PullRequestDetailViewModel(
            ILocalRepositoryModel repository,
            IModelService modelService,
            IPullRequestService pullRequestsService,
            IPackageSettings settings)
        {
            this.repository = repository;
            this.modelService = modelService;
            this.pullRequestsService = pullRequestsService;

            Checkout = ReactiveCommand.CreateAsyncObservable(
                this.WhenAnyValue(x => x.CheckoutState)
                    .Cast<CheckoutCommandState>()
                    .Select(x => x != null && x.DisabledMessage == null), 
                DoCheckout);
            Checkout.ThrownExceptions.Subscribe(x => OperationError = x.Message);

            Pull = ReactiveCommand.CreateAsyncObservable(
                this.WhenAnyValue(x => x.UpdateState)
                    .Cast<UpdateCommandState>()
                    .Select(x => x != null && x.PullDisabledMessage == null),
                DoPull);
            Pull.ThrownExceptions.Subscribe(x => OperationError = x.Message);

            Push = ReactiveCommand.CreateAsyncObservable(
                this.WhenAnyValue(x => x.UpdateState)
                    .Cast<UpdateCommandState>()
                    .Select(x => x != null && x.PushDisabledMessage == null),
                DoPush);
            Push.ThrownExceptions.Subscribe(x => OperationError = x.Message);

            OpenOnGitHub = ReactiveCommand.Create();

            ChangedFilesViewType = (settings.UIState?.PullRequestDetailState?.ShowTree ?? true) ?
                ChangedFilesViewType.TreeView : ChangedFilesViewType.ListView;

            ToggleChangedFilesView = ReactiveCommand.Create();
            ToggleChangedFilesView.Subscribe(_ =>
            {
                ChangedFilesViewType = ChangedFilesViewType == ChangedFilesViewType.TreeView ?
                    ChangedFilesViewType.ListView : ChangedFilesViewType.TreeView;
                settings.UIState.PullRequestDetailState.ShowTree = ChangedFilesViewType == ChangedFilesViewType.TreeView;
                settings.Save();
            });

            OpenChangedFileAction = (settings.UIState?.PullRequestDetailState?.DiffOnOpen ?? true) ?
                OpenChangedFileAction.Diff : OpenChangedFileAction.Open;

            ToggleOpenChangedFileAction = ReactiveCommand.Create();
            ToggleOpenChangedFileAction.Subscribe(_ =>
            {
                OpenChangedFileAction = OpenChangedFileAction == OpenChangedFileAction.Diff ?
                    OpenChangedFileAction.Open : OpenChangedFileAction.Diff;
                settings.UIState.PullRequestDetailState.DiffOnOpen = OpenChangedFileAction == OpenChangedFileAction.Diff;
                settings.Save();
            });

            OpenFile = ReactiveCommand.Create();
            DiffFile = ReactiveCommand.Create();
        }

        /// <summary>
        /// Gets the underlying pull request model.
        /// </summary>
        public IPullRequestModel Model
        {
            get { return model; }
            private set { this.RaiseAndSetIfChanged(ref model, value); }
        }

        /// <summary>
        /// Gets a string describing how to display the pull request's source branch.
        /// </summary>
        public string SourceBranchDisplayName
        {
            get { return sourceBranchDisplayName; }
            private set { this.RaiseAndSetIfChanged(ref sourceBranchDisplayName, value); }
        }

        /// <summary>
        /// Gets a string describing how to display the pull request's target branch.
        /// </summary>
        public string TargetBranchDisplayName
        {
            get { return targetBranchDisplayName; }
            private set { this.RaiseAndSetIfChanged(ref targetBranchDisplayName, value); }
        }

        /// <summary>
        /// Gets the pull request body.
        /// </summary>
        public string Body
        {
            get { return body; }
            private set { this.RaiseAndSetIfChanged(ref body, value); }
        }

        /// <summary>
        /// Gets or sets a value describing how changed files are displayed in a view.
        /// </summary>
        public ChangedFilesViewType ChangedFilesViewType
        {
            get { return changedFilesViewType; }
            set { this.RaiseAndSetIfChanged(ref changedFilesViewType, value); }
        }

        /// <summary>
        /// Gets or sets a value describing how files are opened when double clicked.
        /// </summary>
        public OpenChangedFileAction OpenChangedFileAction
        {
            get { return openChangedFileAction; }
            set { this.RaiseAndSetIfChanged(ref openChangedFileAction, value); }
        }

        /// <summary>
        /// Gets the state associated with the <see cref="Checkout"/> command.
        /// </summary>
        public IPullRequestCheckoutState CheckoutState
        {
            get { return checkoutState; }
            private set { this.RaiseAndSetIfChanged(ref checkoutState, value); }
        }

        /// <summary>
        /// Gets the state associated with the <see cref="Pull"/> and <see cref="Push"/> commands.
        /// </summary>
        public IPullRequestUpdateState UpdateState
        {
            get { return updateState; }
            private set { this.RaiseAndSetIfChanged(ref updateState, value); }
        }

        /// <summary>
        /// Gets the error message to be displayed in the action area as a result of an error in a
        /// git operation.
        /// </summary>
        public string OperationError
        {
            get { return operationError; }
            private set { this.RaiseAndSetIfChanged(ref operationError, value); }
        }

        /// <summary>
        /// Gets the changed files as a tree.
        /// </summary>
        public IReactiveList<IPullRequestChangeNode> ChangedFilesTree { get; } = new ReactiveList<IPullRequestChangeNode>();

        /// <summary>
        /// Gets the changed files as a flat list.
        /// </summary>
        public IReactiveList<IPullRequestFileNode> ChangedFilesList { get; } = new ReactiveList<IPullRequestFileNode>();

        /// <summary>
        /// Gets a command that checks out the pull request locally.
        /// </summary>
        public ReactiveCommand<Unit> Checkout { get; }

        /// <summary>
        /// Gets a command that pulls changes to the current branch.
        /// </summary>
        public ReactiveCommand<Unit> Pull { get; }

        /// <summary>
        /// Gets a command that pushes changes from the current branch.
        /// </summary>
        public ReactiveCommand<Unit> Push { get; }

        /// <summary>
        /// Gets a command that opens the pull request on GitHub.
        /// </summary>
        public ReactiveCommand<object> OpenOnGitHub { get; }

        /// <summary>
        /// Gets a command that toggles the <see cref="ChangedFilesViewType"/> property.
        /// </summary>
        public ReactiveCommand<object> ToggleChangedFilesView { get; }

        /// <summary>
        /// Gets a command that toggles the <see cref="OpenChangedFileAction"/> property.
        /// </summary>
        public ReactiveCommand<object> ToggleOpenChangedFileAction { get; }

        /// <summary>
        /// Gets a command that opens a <see cref="IPullRequestFileNode"/>.
        /// </summary>
        public ReactiveCommand<object> OpenFile { get; }

        /// <summary>
        /// Gets a command that diffs a <see cref="IPullRequestFileNode"/>.
        /// </summary>
        public ReactiveCommand<object> DiffFile { get; }

        /// <summary>
        /// Initializes the view model with new data.
        /// </summary>
        /// <param name="data"></param>
        public override void Initialize([AllowNull] ViewWithData data)
        {
            var prNumber = (int)data.Data;

            IsBusy = true;

            modelService.GetPullRequest(repository, prNumber)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(x => Load(x).Forget());
        }

        /// <summary>
        /// Loads the view model from octokit models.
        /// </summary>
        /// <param name="pullRequest">The pull request model.</param>
        /// <param name="files">The pull request's changed files.</param>
        public async Task Load(IPullRequestModel pullRequest)
        {
            Model = pullRequest;
            Title = Resources.PullRequestNavigationItemText + " #" + pullRequest.Number;
            SourceBranchDisplayName = GetBranchDisplayName(pullRequest.Head?.Label);
            TargetBranchDisplayName = GetBranchDisplayName(pullRequest.Base.Label);
            Body = !string.IsNullOrWhiteSpace(pullRequest.Body) ? pullRequest.Body : "*No description provided.*";

            ChangedFilesTree.Clear();
            ChangedFilesList.Clear();

            // WPF doesn't support AddRange here so iterate through the changes.
            foreach (var change in CreateChangedFilesList(pullRequest.ChangedFiles))
            {
                ChangedFilesList.Add(change);
            }

            foreach (var change in CreateChangedFilesTree(ChangedFilesList).Children)
            {
                ChangedFilesTree.Add(change);
            }

            var localBranches = await pullRequestsService.GetLocalBranches(repository, pullRequest).ToList();
            var isCheckedOut = localBranches.Contains(repository.CurrentBranch);

            if (isCheckedOut)
            {
                var divergence = await pullRequestsService.CalculateHistoryDivergence(repository, Model.Number);
                var pullDisabled = divergence.BehindBy == 0 ? "No commits to pull" : null;
                var pushDisabled = divergence.AheadBy == 0 ? 
                    "No commits to push" : 
                    divergence.BehindBy > 0 ? "You must pull before you can push" : null;

                UpdateState = new UpdateCommandState(divergence, pullDisabled, pushDisabled);
                CheckoutState = null;
            }
            else
            {
                var caption = localBranches.Count > 0 ?
                    "Checkout " + localBranches.First().DisplayName :
                    "Checkout to " + (await pullRequestsService.GetDefaultLocalBranchName(repository, Model.Number, Model.Title));
                var disabled = await pullRequestsService.IsWorkingDirectoryClean(repository) ?
                    null :
                    "Cannot checkout as your working directory has uncommitted changes.";

                CheckoutState = new CheckoutCommandState(caption, disabled);
                UpdateState = null;
            }

            IsBusy = false;
        }

        /// <summary>
        /// Gets the specified file as it appears in the pull request.
        /// </summary>
        /// <param name="file">The file or directory node.</param>
        /// <returns>The path to the extracted file.</returns>
        public Task<string> ExtractFile(IPullRequestFileNode file)
        {
            var path = Path.Combine(file.DirectoryPath, file.FileName);
            return pullRequestsService.ExtractFile(repository, model.Head.Sha, path).ToTask();
        }

        /// <summary>
        /// Gets the before and after files needed for viewing a diff.
        /// </summary>
        /// <param name="file">The changed file.</param>
        /// <returns>A tuple containing the full path to the before and after files.</returns>
        public Task<Tuple<string, string>> ExtractDiffFiles(IPullRequestFileNode file)
        {
            var path = Path.Combine(file.DirectoryPath, file.FileName);
            return pullRequestsService.ExtractDiffFiles(repository, model, path).ToTask();
        }

        IEnumerable<IPullRequestFileNode> CreateChangedFilesList(IEnumerable<IPullRequestFileModel> files)
        {
            return files.Select(x => new PullRequestFileNode(repository.LocalPath, x.FileName, x.Status));
        }

        static IPullRequestDirectoryNode CreateChangedFilesTree(IEnumerable<IPullRequestFileNode> files)
        {
            var dirs = new Dictionary<string, PullRequestDirectoryNode>
            {
                { string.Empty, new PullRequestDirectoryNode(string.Empty) }
            };

            foreach (var file in files)
            {
                var dir = GetDirectory(file.DirectoryPath, dirs);
                dir.Files.Add(file);
            }

            return dirs[string.Empty];
        }

        static PullRequestDirectoryNode GetDirectory(string path, Dictionary<string, PullRequestDirectoryNode> dirs)
        {
            PullRequestDirectoryNode dir;

            if (!dirs.TryGetValue(path, out dir))
            {
                var parentPath = Path.GetDirectoryName(path);
                var parentDir = GetDirectory(parentPath, dirs);

                dir = new PullRequestDirectoryNode(path);

                if (!parentDir.Directories.Any(x => x.DirectoryName == dir.DirectoryName))
                {
                    parentDir.Directories.Add(dir);
                    dirs.Add(path, dir);
                }
            }

            return dir;
        }

        string GetBranchDisplayName(string targetBranchLabel)
        {
            if (targetBranchLabel != null)
            {
                var parts = targetBranchLabel.Split(':');
                var owner = parts[0];
                return owner == repository.CloneUrl.Owner ? parts[1] : targetBranchLabel;
            }
            else
            {
                return "[Invalid]";
            }
        }

        IObservable<Unit> DoCheckout(object unused)
        {
            return Observable.Defer(async () =>
            {
                var localBranches = await pullRequestsService.GetLocalBranches(repository, Model).ToList();

                if (localBranches.Count > 0)
                {
                    return pullRequestsService.SwitchToBranch(repository, Model);
                }
                else
                {
                    return pullRequestsService
                        .GetDefaultLocalBranchName(repository, Model.Number, Model.Title)
                        .SelectMany(x => pullRequestsService.Checkout(repository, Model, x));
                }
            });
        }

        IObservable<Unit> DoPull(object unused)
        {
            return pullRequestsService.Pull(repository);
        }

        IObservable<Unit> DoPush(object unused)
        {
            return pullRequestsService.Push(repository);
        }

        class CheckoutCommandState : IPullRequestCheckoutState
        {
            public CheckoutCommandState(string caption, string disabledMessage)
            {
                Caption = caption;
                DisabledMessage = disabledMessage;
            }

            public string Caption { get; }
            public string DisabledMessage { get; }
        }

        class UpdateCommandState : IPullRequestUpdateState
        {
            public UpdateCommandState(BranchTrackingDetails divergence, string pullDisabledMessage, string pushDisabledMessage)
            {
                CommitsAhead = divergence.AheadBy ?? 0;
                CommitsBehind = divergence.BehindBy ?? 0;
                PullDisabledMessage = pullDisabledMessage;
                PushDisabledMessage = pushDisabledMessage;
            }

            public int CommitsAhead { get; }
            public int CommitsBehind { get; }
            public bool UpToDate => CommitsAhead == 0 && CommitsBehind == 0;
            public string PullDisabledMessage { get; }
            public string PushDisabledMessage { get; }
        }
    }
}
