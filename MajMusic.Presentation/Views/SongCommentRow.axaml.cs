#nullable enable

using Avalonia.Controls;

namespace MajMusic.Presentation.Views
{
    /// <summary>Shares a source comment's text and actions between desktop discussions and the mobile detail sheet.</summary>
    public partial class SongCommentRow : UserControl
    {
        /// <summary>Creates a plain-text comment row without starting network work.</summary>
        public SongCommentRow()
        {
            InitializeComponent();
        }
    }
}
