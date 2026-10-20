using Booktibus.Models;

namespace Booktibus.Services
{
    /// <summary>
    /// Business-layer authorization service that enforces role permissions and Reader data isolation.
    /// Prevents unauthorized operations even if invoked directly bypassing UI visibility.
    /// </summary>
    public class AuthorizationService
    {
        private readonly SessionService _sessionService;

        private static readonly Dictionary<UserRole, HashSet<AppPermission>> RolePermissions = new()
        {
            [UserRole.Reader] =
            [
                AppPermission.ViewDashboard,
                AppPermission.BrowseCatalog,
                AppPermission.ViewOwnBorrowingAndFines,
                AppPermission.ManageOwnFavorites,
                AppPermission.ManageOwnProfile
            ],
            [UserRole.Librarian] =
            [
                AppPermission.ViewDashboard,
                AppPermission.BrowseCatalog,
                AppPermission.ManageCatalog,
                AppPermission.ManageReaders,
                AppPermission.ViewOwnBorrowingAndFines,
                AppPermission.ProcessCirculation,
                AppPermission.ManageAllFines,
                AppPermission.ViewReports,
                AppPermission.ManageOwnFavorites,
                AppPermission.ManageOwnProfile
            ],
            [UserRole.Administrator] =
            [
                AppPermission.ViewDashboard,
                AppPermission.BrowseCatalog,
                AppPermission.ManageCatalog,
                AppPermission.ManageReaders,
                AppPermission.ViewOwnBorrowingAndFines,
                AppPermission.ProcessCirculation,
                AppPermission.ManageAllFines,
                AppPermission.ViewReports,
                AppPermission.ManageSystemSettings,
                AppPermission.ManageOwnFavorites,
                AppPermission.ManageOwnProfile
            ]
        };

        private static readonly Dictionary<NavigationPageKey, HashSet<UserRole>> PageRoleMatrix = new()
        {
            [NavigationPageKey.Dashboard] = [UserRole.Reader, UserRole.Librarian, UserRole.Administrator],
            [NavigationPageKey.Books] = [UserRole.Reader, UserRole.Librarian, UserRole.Administrator],
            [NavigationPageKey.Favorites] = [UserRole.Reader],
            [NavigationPageKey.Readers] = [UserRole.Librarian, UserRole.Administrator],
            [NavigationPageKey.BorrowingReturns] = [UserRole.Reader, UserRole.Librarian, UserRole.Administrator],
            [NavigationPageKey.ReaderProfile] = [UserRole.Reader, UserRole.Librarian, UserRole.Administrator],
            [NavigationPageKey.Fines] = [UserRole.Reader, UserRole.Librarian, UserRole.Administrator],
            [NavigationPageKey.Reports] = [UserRole.Librarian, UserRole.Administrator],
            [NavigationPageKey.Settings] = [UserRole.Administrator]
        };

        public AuthorizationService(SessionService sessionService)
        {
            ArgumentNullException.ThrowIfNull(sessionService);
            _sessionService = sessionService;
        }

        public bool HasPermission(AppPermission permission)
        {
            if (_sessionService.CurrentSession is not { } session)
            {
                return false;
            }

            return RolePermissions.TryGetValue(session.Role, out HashSet<AppPermission>? perms) &&
                   perms.Contains(permission);
        }

        public bool CanAccessPage(NavigationPageKey pageKey)
        {
            if (_sessionService.CurrentSession is not { } session)
            {
                return false;
            }

            return PageRoleMatrix.TryGetValue(pageKey, out HashSet<UserRole>? allowedRoles) &&
                   allowedRoles.Contains(session.Role);
        }

        public static IReadOnlyCollection<UserRole> GetAllowedRolesForPage(NavigationPageKey pageKey)
        {
            if (PageRoleMatrix.TryGetValue(pageKey, out HashSet<UserRole>? roles))
            {
                return roles.ToList();
            }

            return [];
        }

        /// <summary>
        /// Enforces that a Reader can only access data belonging to their own authenticated UserId,
        /// even if two accounts share the same Email address.
        /// </summary>
        public bool CanAccessUserAccountData(int targetUserId, out string denialMessage)
        {
            if (_sessionService.CurrentSession is not { } session)
            {
                denialMessage = LocalizationService.IsVietnamese
                    ? "[Từ chối truy cập] Chưa có phiên đăng nhập hợp lệ."
                    : "[Access Denied] No active authenticated session.";
                return false;
            }

            if (session.UserId == targetUserId)
            {
                denialMessage = string.Empty;
                return true;
            }

            if (session.Role is UserRole.Librarian or UserRole.Administrator)
            {
                denialMessage = string.Empty;
                return true;
            }

            denialMessage = LocalizationService.IsVietnamese
                ? $"[Chặn ở tầng Service] Tài khoản '{session.Username}' (UserId={session.UserId}) không được phép truy cập dữ liệu riêng của UserId={targetUserId}."
                : $"[Blocked by Service Layer] Account '{session.Username}' (UserId={session.UserId}) is not authorized to access private data of UserId={targetUserId}.";
            return false;
        }

        /// <summary>
        /// Enforces that a Reader can only view personal profile, borrowing history, and fines
        /// belonging to their own ReaderCardId, whereas Librarian and Administrator can access all reader records.
        /// </summary>
        public bool CanAccessReaderRecord(string targetReaderCardId, out string denialMessage)
        {
            if (_sessionService.CurrentSession is not { } session)
            {
                denialMessage = LocalizationService.IsVietnamese
                    ? "[Từ chối truy cập] Chưa có phiên đăng nhập hợp lệ."
                    : "[Access Denied] No active authenticated session.";
                return false;
            }

            if (session.Role is UserRole.Librarian or UserRole.Administrator)
            {
                denialMessage = string.Empty;
                return true;
            }

            // Reader role: strictly restricted to their own ReaderCardId
            if (session.Role == UserRole.Reader &&
                !string.IsNullOrWhiteSpace(session.ReaderCardId) &&
                string.Equals(session.ReaderCardId, targetReaderCardId, StringComparison.OrdinalIgnoreCase))
            {
                denialMessage = string.Empty;
                return true;
            }

            denialMessage = LocalizationService.IsVietnamese
                ? $"[Chặn ở tầng Service] Vai trò Độc giả ({session.ReaderCardId}) chỉ được phép xem hồ sơ, lịch sử mượn trả và tiền phạt của chính mình. Không được phép truy cập dữ liệu của thẻ '{targetReaderCardId}'."
                : $"[Blocked by Service Layer] Reader role ({session.ReaderCardId}) is strictly restricted to their own profile, borrowing history, and fines. Access to card '{targetReaderCardId}' was denied.";
            return false;
        }

        /// <summary>
        /// Enforces a business permission at the service layer before executing an operation.
        /// </summary>
        public bool TryAuthorizeOperation(AppPermission requiredPermission, string operationName, out string resultMessage)
        {
            if (_sessionService.CurrentSession is not { } session)
            {
                resultMessage = LocalizationService.IsVietnamese
                    ? "[Từ chối ở tầng Service] Vui lòng đăng nhập trước khi thực hiện thao tác."
                    : "[Service Layer Denied] Please sign in before performing this operation.";
                return false;
            }

            if (!HasPermission(requiredPermission))
            {
                resultMessage = LocalizationService.IsVietnamese
                    ? $"[Chặn ở tầng Service] Vai trò '{session.Role}' không được phép gọi chức năng '{operationName}' (Yêu cầu quyền: {requiredPermission})."
                    : $"[Blocked by Service Layer] Role '{session.Role}' is not authorized to invoke '{operationName}' (Requires permission: {requiredPermission}).";
                return false;
            }

            resultMessage = LocalizationService.IsVietnamese
                ? $"[Phân quyền Hợp lệ — Tầng Service] Vai trò '{session.Role}' được phép thực thi '{operationName}' ({requiredPermission})."
                : $"[Service Layer Authorized] Role '{session.Role}' is permitted to execute '{operationName}' ({requiredPermission}).";
            return true;
        }
    }
}

