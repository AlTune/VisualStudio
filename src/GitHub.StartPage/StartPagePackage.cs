﻿using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using GitHub.Services;
using GitHub.UI;
using GitHub.ViewModels;
using System.IO;
using Microsoft.TeamFoundation.Controls;
using Microsoft.TeamFoundation.Git.Controls.Extensibility;
using Microsoft.VisualStudio.Shell.CodeContainerManagement;
using ICodeContainerProvider = Microsoft.VisualStudio.Shell.CodeContainerManagement.ICodeContainerProvider;
using CodeContainer = Microsoft.VisualStudio.Shell.CodeContainerManagement.CodeContainer;
using Task = System.Threading.Tasks.Task;
using System.ComponentModel;
using GitHub.Models;
using GitHub.Extensions;
using GitHub.Primitives;
using GitHub.VisualStudio;

namespace GitHub.StartPage
{
    [PackageRegistration(UseManagedResourcesOnly = true)]
    [Guid(Guids.StartPagePackageId)]
    [ProvideCodeContainerProvider("GitHub Container", Guids.StartPagePackageId, Guids.ImagesId, 1, "#110", "#111", typeof(GitHubContainerProvider))]
    public sealed class StartPagePackage : ExtensionPointPackage
    {
        static IServiceProvider serviceProvider;
        internal static IServiceProvider ServiceProvider { get { return serviceProvider; } }

        public StartPagePackage()
        {
            serviceProvider = this;
        }
    }

    [Guid(Guids.CodeContainerProviderId)]
    public class GitHubContainerProvider : ICodeContainerProvider
    {
        public async Task<CodeContainer> AcquireCodeContainerAsync(IProgress<ServiceProgressData> downloadProgress, CancellationToken cancellationToken)
        {

            return await RunAcquisition(downloadProgress, cancellationToken, null);
        }

        public async Task<CodeContainer> AcquireCodeContainerAsync(RemoteCodeContainer onlineCodeContainer, IProgress<ServiceProgressData> downloadProgress, CancellationToken cancellationToken)
        {
            var repository = new RepositoryModel(onlineCodeContainer.Name, UriString.ToUriString(onlineCodeContainer.DisplayUrl));
            return await RunAcquisition(downloadProgress, cancellationToken, repository);
        }

        async Task<CodeContainer> RunAcquisition(IProgress<ServiceProgressData> downloadProgress, CancellationToken cancellationToken, IRepositoryModel repository)
        {
            CloneRequest request = null;

            try
            {
                var uiProvider = await Task.Run(() => Package.GetGlobalService(typeof(IGitHubServiceProvider)) as IGitHubServiceProvider);
                var cm = uiProvider.TryGetService<IConnectionManager>();
                var gitRepositories = await GetGitRepositoriesExt(uiProvider);
                request = ShowCloneDialog(uiProvider, gitRepositories, repository);
            }
            catch
            {
                // TODO: log
            }

            if (request == null)
                return null;

            var path = Path.Combine(request.BasePath, request.Repository.Name);
            var uri = request.Repository.CloneUrl.ToRepositoryUrl();
            return new CodeContainer(
                localProperties: new CodeContainerLocalProperties(path, CodeContainerType.Folder,
                                new CodeContainerSourceControlProperties(request.Repository.Name, path, new Guid(Guids.GitSccProviderId))),
                remote: new RemoteCodeContainer(request.Repository.Name,
                                                new Guid(Guids.CodeContainerProviderId),
                                                uri,
                                                new Uri(uri.ToString().TrimSuffix(".git")),
                                                DateTimeOffset.UtcNow),
                isFavorite: false,
                lastAccessed: DateTimeOffset.UtcNow);
        }

        async Task<IGitRepositoriesExt> GetGitRepositoriesExt(IGitHubServiceProvider gitHubServiceProvider)
        {
            var page = await GetTeamExplorerPage(gitHubServiceProvider);
            return page?.GetService<IGitRepositoriesExt>();
        }

        async Task<ITeamExplorerPage> GetTeamExplorerPage(IGitHubServiceProvider gitHubServiceProvider)
        {
            var te = gitHubServiceProvider?.GetService(typeof(ITeamExplorer)) as ITeamExplorer;

            if (te != null)
            {
                var page = te.NavigateToPage(new Guid(TeamExplorerPageIds.Connect), null);

                if (page == null)
                {
                    var tcs = new TaskCompletionSource<ITeamExplorerPage>();
                    PropertyChangedEventHandler handler = null;

                    handler = new PropertyChangedEventHandler((s, e) =>
                    {
                        if (e.PropertyName == "CurrentPage")
                        {
                            tcs.SetResult(te.CurrentPage);
                            te.PropertyChanged -= handler;
                        }
                    });

                    te.PropertyChanged += handler;

                    page = await tcs.Task;
                }

                return page;
            }
            else
            {
                // TODO: Log
                return null;
            }
        }

        CloneRequest ShowCloneDialog(IGitHubServiceProvider gitHubServiceProvider, IGitRepositoriesExt gitRepositories, IRepositoryModel repository = null)
        {
            string basePath = null;

            gitHubServiceProvider.AddService(this, gitRepositories);
            var uiProvider = gitHubServiceProvider.GetService<IUIProvider>();
            var controller = uiProvider.Configure(repository == null ? UIControllerFlow.Clone : UIControllerFlow.StartPageClone,
                null //TODO: set the connection corresponding to the repository if the repository is not null
                );
            controller.TransitionSignal.Subscribe(x =>
            {
                if ((repository == null && x.Data.ViewType == Exports.UIViewType.Clone) || // fire the normal clone dialog
                    (repository != null && x.Data.ViewType == Exports.UIViewType.StartPageClone) // fire the clone dialog for re-acquiring a repo
                   )
                {
                    var vm = x.View.ViewModel as IBaseCloneViewModel;
                    if (repository != null)
                        vm.SelectedRepository = repository;
                    x.View.Done.Subscribe(_ =>
                    {
                        basePath = vm.BaseRepositoryPath;
                        if (repository == null)
                            repository = vm.SelectedRepository;
                    });
                }
            });

            uiProvider.RunInDialog(controller);
            gitHubServiceProvider.RemoveService(typeof(IGitRepositoriesExt), this);

            return repository != null && basePath != null ? new CloneRequest(basePath, repository) : null;
        }

        class CloneRequest
        {
            public CloneRequest(string basePath, IRepositoryModel repository)
            {
                BasePath = basePath;
                Repository = repository;
            }

            public string BasePath { get; }
            public IRepositoryModel Repository { get; }
        }
    }
}
