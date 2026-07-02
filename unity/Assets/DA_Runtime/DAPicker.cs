using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DA
{
    /// <summary>
    /// A self-contained dropdown/picker built entirely in code (Unity's DefaultControls dropdown
    /// list refused to render its item text here). Header button shows the current value; clicking
    /// opens a scrollable list of light rows with dark text. Fully controlled = always readable.
    /// </summary>
    public class DAPicker
    {
        public int Value { get; private set; }
        public Action<int> onChanged;
        public string CurrentText => (_opts != null && Value >= 0 && Value < _opts.Count) ? _opts[Value] : "";

        private readonly List<string> _opts = new List<string>();
        private readonly Font _font;
        private Text _label;
        private RectTransform _header;
        private GameObject _popup;
        private RectTransform _content;
        private bool _open;

        private static readonly Color BoxCol = new Color(0.95f, 0.95f, 0.97f, 0.98f);
        private static readonly Color RowCol = new Color(0.90f, 0.90f, 0.93f, 1f);
        private static readonly Color TxtCol = new Color(0.10f, 0.10f, 0.13f);

        public DAPicker(RectTransform parent, Font font, float yTop, float height)
        {
            _font = font;

            var go = new GameObject("Picker", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            _header = (RectTransform)go.transform;
            _header.anchorMin = new Vector2(0, 1); _header.anchorMax = new Vector2(1, 1); _header.pivot = new Vector2(0.5f, 1);
            _header.offsetMin = new Vector2(12, 0); _header.offsetMax = new Vector2(-12, 0);
            _header.sizeDelta = new Vector2(_header.sizeDelta.x, height);
            _header.anchoredPosition = new Vector2(0, yTop);
            go.GetComponent<Image>().color = BoxCol;

            _label = MkText(go.transform, "", TextAnchor.MiddleLeft);
            var lrt = (RectTransform)_label.transform;
            lrt.offsetMin = new Vector2(10, 0); lrt.offsetMax = new Vector2(-22, 0);
            var arrow = MkText(go.transform, "▼", TextAnchor.MiddleRight);
            ((RectTransform)arrow.transform).offsetMax = new Vector2(-8, 0);

            go.GetComponent<Button>().onClick.AddListener(Toggle);
        }

        private Text MkText(Transform parent, string s, TextAnchor anchor)
        {
            var t = new GameObject("T", typeof(RectTransform)).AddComponent<Text>();
            t.transform.SetParent(parent, false);
            t.font = _font; t.fontSize = 17; t.alignment = anchor; t.color = TxtCol;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Truncate;
            t.text = s;
            var rt = (RectTransform)t.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            return t;
        }

        public void SetOptions(IEnumerable<string> options)
        {
            _opts.Clear(); _opts.AddRange(options);
            if (Value >= _opts.Count) Value = _opts.Count - 1;
            if (Value < 0) Value = 0;
            _label.text = CurrentText;
            if (_open) Close();
            if (_popup != null) { UnityEngine.Object.Destroy(_popup); _popup = null; }
        }

        public void SetValueWithoutNotify(int i)
        {
            if (_opts.Count == 0) return;
            Value = Mathf.Clamp(i, 0, _opts.Count - 1);
            _label.text = CurrentText;
        }

        private void Select(int i)
        {
            SetValueWithoutNotify(i);
            Close();
            onChanged?.Invoke(Value);
        }

        public void Toggle() { if (_open) Close(); else Open(); }

        private void Close()
        {
            _open = false;
            if (_popup != null) _popup.SetActive(false);
        }

        private void Open()
        {
            if (_opts.Count == 0) return;
            if (_popup == null) BuildPopup();
            else RebuildRows();
            _popup.SetActive(true);
            _popup.transform.SetAsLastSibling();
            _open = true;
        }

        // popup is parented to the header's parent (the debug panel) but overflows it (no clip);
        // positioned just below the header.
        private void BuildPopup()
        {
            float listH = Mathf.Min(320f, _opts.Count * 26f + 8f);
            _popup = new GameObject("PickerPopup", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            _popup.transform.SetParent(_header.parent, false);
            var prt = (RectTransform)_popup.transform;
            prt.anchorMin = new Vector2(0, 1); prt.anchorMax = new Vector2(1, 1); prt.pivot = new Vector2(0.5f, 1);
            prt.offsetMin = new Vector2(12, 0); prt.offsetMax = new Vector2(-12, 0);
            prt.sizeDelta = new Vector2(prt.sizeDelta.x, listH);
            prt.anchoredPosition = new Vector2(0, _header.anchoredPosition.y - _header.sizeDelta.y - 2);
            _popup.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.15f, 0.99f);

            var vp = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            vp.transform.SetParent(_popup.transform, false);
            var vrt = (RectTransform)vp.transform;
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one; vrt.offsetMin = new Vector2(4, 4); vrt.offsetMax = new Vector2(-4, -4);
            vp.GetComponent<Image>().color = new Color(1, 1, 1, 0.02f);

            var contentGO = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGO.transform.SetParent(vp.transform, false);
            _content = (RectTransform)contentGO.transform;
            _content.anchorMin = new Vector2(0, 1); _content.anchorMax = new Vector2(1, 1); _content.pivot = new Vector2(0.5f, 1);
            // Normalize the horizontal size/position so Content == viewport width (height stays driven by
            // the ContentSizeFitter). Otherwise a leftover sizeDelta.x makes Content overhang the viewport
            // and the RectMask2D clips the LEFT of every row's text.
            _content.sizeDelta = new Vector2(0f, _content.sizeDelta.y);
            _content.anchoredPosition = new Vector2(0f, _content.anchoredPosition.y);
            var vlg = contentGO.GetComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true; vlg.childControlHeight = true; vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.spacing = 2;
            contentGO.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var sr = _popup.GetComponent<ScrollRect>();
            sr.viewport = vrt; sr.content = _content; sr.horizontal = false; sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped; sr.scrollSensitivity = 24;

            RebuildRows();
        }

        private void RebuildRows()
        {
            for (int i = _content.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
            for (int i = 0; i < _opts.Count; i++)
            {
                int idx = i;
                var row = new GameObject("Row", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
                row.transform.SetParent(_content, false);
                row.GetComponent<Image>().color = (i == Value) ? new Color(0.30f, 0.45f, 0.65f, 1f) : RowCol;
                row.GetComponent<LayoutElement>().minHeight = 24; row.GetComponent<LayoutElement>().preferredHeight = 24;
                row.GetComponent<Button>().onClick.AddListener(() => Select(idx));
                var t = MkText(row.transform, _opts[i], TextAnchor.MiddleLeft);
                var trt = (RectTransform)t.transform;
                trt.offsetMin = new Vector2(10, 0); trt.offsetMax = new Vector2(-8, 0);
            }
        }
    }
}
