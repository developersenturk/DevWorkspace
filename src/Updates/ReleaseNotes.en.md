## Double-click and update notification fixes

- Double-click rechecks Git status. Changed projects open their diff; clean projects and projects without a Git repository open normally.
- Update checks start immediately on launch. A visible banner shows the check in progress, then download and restart status when a newer version is found.
- Failed checks keep a visible retry action. While the app stays open, checks repeat every 15 minutes, or after one minute following a failure.
