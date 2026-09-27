using System.Collections.Generic;

namespace NxeDashboard.Runtime
{
    public sealed class XuiBindings
    {
        public static XuiBindings Empty { get; } = new XuiBindings();

        public IDictionary<int, string> Images { get; } = new Dictionary<int, string>();
        public IDictionary<int, string> Text { get; } = new Dictionary<int, string>();
        public IDictionary<string, string> TextByElement { get; } =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        public static XuiBindings ForSlot(string title, string subtitle, string background, string icon)
        {
            var result = new XuiBindings();
            result.Images[0] = background;
            result.Images[1] = icon;
            result.Images[20] = icon;
            result.Text[1] = subtitle;
            result.TextByElement["XuiTextPresenter1"] = title;
            result.TextByElement["XuiTextPresenter2"] = subtitle;
            return result;
        }
    }
}
