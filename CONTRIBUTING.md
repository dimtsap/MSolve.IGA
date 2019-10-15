
# How to contribute
MGroup is striving to write code related to the solution of computational mechanics and simulations, of utmost quality and performance. Since this code reflects all of the research work being done in the MGroup lab, all of the repos are being constantly updated. We want to make make contributing to this project as easy and transparent as possible, but there is more work to be done. Hopefully this document makes the process for contributing clear and answers some questions that you may have.

## Code of conduct
MGroup has adopted the [Contributor Covenant](https://www.contributor-covenant.org/) as its Code of Conduct, and we expect project participants to adhere to it. Please read the [full text](CODE_OF_CONDUCT.md) so that you can understand what actions will and will not be tolerated.

## Open development
All work on the repos of MGroup happens directly on [GitHub](https://www.github.com/mgroupntua/). Both core team members and external contributors send pull requests which go through the same review process.

## Semantic versioning
MGroup follows semantic versioning in all of its repos. We release patch versions for critical bugfixes, minor versions for new features or non-essential changes, and major versions for any breaking changes. When we make breaking changes, we also introduce deprecation warnings in a minor version so that our users learn about the upcoming changes and migrate their code in advance. Learn more about our commitment to stability and incremental migration in our [versioning policy](versioning.md). Every significant change is documented in the changelog file.

## Branch organization
All repos have a master, a develop and a gh-pages branch. For both master and gh-pages branches, tags are used in order to signify the commits related to specific versions. The develop branch also uses the aforementioned tags and contains features related to our development roadmap. 
Submit all changes directly to the develop branch. We do our best to keep develop in the same good shape as master, with all tests passing. Code that lands in develop must be compatible with the latest stable release. It may contain additional features, but no breaking changes. We should be able to make a pull request to master, releasing a new minor version from the tip of develop at any time.

## Bugs
### Where to find known issues
We are using GitHub Issues for each project for our public bugs. We keep a close eye on this and try to make it clear when we have an internal fix in progress. Before filing a new task, try to make sure your problem doesn’t already exist.

### Reporting new issues
The best way to get your bug fixed is to provide a reduced test case, providing all related code.

## Contact
Please send an e-mail at [info (at) mgroup.ntua.gr](info@mgroup.ntua.gr) and we will get in touch as soon as possible.

## Proposing a change
If you intend to change the public API, or make any non-trivial changes to the implementation, we recommend filing an issue. This lets us reach an agreement on your proposal before you put significant effort into it.
If you’re only fixing a bug, it’s fine to submit a pull request right away but we still recommend to file an issue detailing what you’re fixing. This is helpful in case we don’t accept that specific fix but want to keep track of the issue.

## Your first pull request
MGroup adheres to the [GitHub flow](https://guides.github.com/introduction/flow/) standard with the Fork and Pull variant as seen [here](https://help.github.com/en/articles/about-collaborative-development-models).

### Separate branch per pull request
We recommend that you create a separate branch for each pull request, as opposed to using master. This makes it much easier to continue working on a pull request even after it has been opened on GitHub. Remember that GitHub automatically includes all future commits of the same branch to the pull request.

### Focused
We prefer a couple small pull requests over a single large one that targets multiple things at once.

### Sending a pull request
The core team is monitoring for pull requests. We will review your pull request and either merge it, request changes to it, or close it with an explanation. For API changes we may need to fix our internal uses at Facebook.com, which could cause some delay. We’ll do our best to provide updates and feedback throughout the process.
If somebody claims an issue but doesn’t follow up for more than two weeks, it’s fine to take it over but you should still leave a comment.

Before submitting a pull request, please make sure that all tests pass. Furthermore, we require that your pull request contains unit tests for any new functionality. This way we can ensure that we don’t break your code in the future.

## Style guide
We use an automatic code formatter called Prettier. Run yarn prettier after making any changes to the code.

## Request for Comments (RFC)
Many changes, including bug fixes and documentation improvements can be implemented and reviewed via the normal GitHub pull request workflow.
Some changes though are “substantial”, and we ask that these be put through a bit of a design process and produce a consensus among the MGroup core team.

## License
By contributing to MGroup repos, you agree that your contributions will be licensed under its MIT license, unless stated otherwise in a specific repo.
