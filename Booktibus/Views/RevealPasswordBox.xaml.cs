using System.Windows;
using System.Windows.Controls;

namespace Booktibus.Views
{
    /// <summary>
    /// Interaction logic for RevealPasswordBox.xaml
    /// Reusable password input box with a built-in Eye toggle button to show/hide plaintext password.
    /// Fully compatible with WPF MVVM two-way binding, standard styling, and focus preservation.
    /// </summary>
    public partial class RevealPasswordBox : UserControl
    {
        private bool _isUpdating;

        public static readonly DependencyProperty PasswordProperty =
            DependencyProperty.Register(
                nameof(Password),
                typeof(string),
                typeof(RevealPasswordBox),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnPasswordPropertyChanged));

        public static readonly DependencyProperty IsPasswordVisibleProperty =
            DependencyProperty.Register(
                nameof(IsPasswordVisible),
                typeof(bool),
                typeof(RevealPasswordBox),
                new FrameworkPropertyMetadata(
                    false,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public string Password
        {
            get => (string)GetValue(PasswordProperty);
            set => SetValue(PasswordProperty, value);
        }

        public bool IsPasswordVisible
        {
            get => (bool)GetValue(IsPasswordVisibleProperty);
            set => SetValue(IsPasswordVisibleProperty, value);
        }

        public RevealPasswordBox()
        {
            InitializeComponent();
        }

        private static void OnPasswordPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RevealPasswordBox control && !control._isUpdating)
            {
                string newPassword = e.NewValue as string ?? string.Empty;
                control.SyncPassword(newPassword);
            }
        }

        private void SyncPassword(string newPassword)
        {
            if (InternalPasswordBox == null || InternalTextBox == null)
            {
                return;
            }

            _isUpdating = true;
            try
            {
                if (InternalPasswordBox.Password != newPassword)
                {
                    InternalPasswordBox.Password = newPassword;
                }
                if (InternalTextBox.Text != newPassword)
                {
                    InternalTextBox.Text = newPassword;
                }
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void InternalPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isUpdating || InternalPasswordBox == null || InternalTextBox == null)
            {
                return;
            }

            _isUpdating = true;
            try
            {
                Password = InternalPasswordBox.Password;
                InternalTextBox.Text = InternalPasswordBox.Password;
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void InternalTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || InternalPasswordBox == null || InternalTextBox == null)
            {
                return;
            }

            _isUpdating = true;
            try
            {
                Password = InternalTextBox.Text;
                InternalPasswordBox.Password = InternalTextBox.Text;
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void EyeButton_OnClick(object sender, RoutedEventArgs e)
        {
            IsPasswordVisible = !IsPasswordVisible;
            if (IsPasswordVisible)
            {
                InternalTextBox.Focus();
                InternalTextBox.CaretIndex = InternalTextBox.Text.Length;
            }
            else
            {
                InternalPasswordBox.Focus();
            }
        }
    }
}

