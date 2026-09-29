using UnityEngine;
using UnityEngine.UI;

namespace XRCollab.Measurement.Mirroring
{
    /// <summary>
    /// Muestra el espejo en la ventana del PC: la imagen a pantalla completa (con su proporción) y una línea de
    /// estado abajo a la derecha, en un Canvas Screen Space - Overlay.
    ///   - Overlay no se renderiza en los ojos del visor: solo lo ve quien mira el laptop. Unity avisa una vez
    ///     en el log ("...will not be visible while in VR"): es justo lo que se busca.
    ///   - Los HUD existentes (auditoría, rendimiento, modo medición) usan OnGUI, que se dibuja después de todos
    ///     los Canvas, así que la imagen queda siempre debajo de ellos. (Dibujarla con OnGUI no sirve: GUI.depth
    ///     no ordena de forma confiable entre scripts y la imagen tapaba esos HUD.)
    /// No sabe de dónde vienen los cuadros; <see cref="HeadsetMirror"/> se los entrega.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HeadsetMirrorView : MonoBehaviour
    {
        private const int CanvasSortingOrder = -1000;
        private const int StatusFontSize = 18;
        private const int NoticeTitleFontSize = 42;
        private const int NoticeHintFontSize = 24;
        private const float NoticeWidth = 1250f;
        private static readonly Vector2 ReferenceResolution = new Vector2(1920, 1080);
        private static readonly Color DimmedImage = new Color(0.35f, 0.35f, 0.35f, 1f);

        private RawImage _image;
        private AspectRatioFitter _fitter;
        private Text _status;
        private GameObject _noticePanel;
        private Text _noticeTitle;
        private Text _noticeHint;
        private MirrorNotice _notice;
        private Texture2D _texture;
        private MirrorLayout _layout = MirrorLayout.BothEyes;
        private bool _visible = true;
        private bool _hasFrame;

        public MirrorLayout Layout
        {
            get => _layout;
            set { _layout = value; ApplyLayout(); }
        }

        public bool Visible
        {
            get => _visible;
            set { _visible = value; RefreshImage(); }
        }

        /// <summary>Sube un cuadro RGBA (filas de abajo hacia arriba) a la textura y lo muestra.</summary>
        public void Present(byte[] rgba, int width, int height)
        {
            EnsureTexture(width, height);
            _texture.LoadRawTextureData(rgba);
            _texture.Apply(false, false);
            if (!_hasFrame)
            {
                _hasFrame = true;
                RefreshImage();
            }
        }

        /// <summary>Oculta la imagen (sin sesión no se deja un cuadro viejo que parezca en vivo).</summary>
        public void ClearFrame()
        {
            if (!_hasFrame) return;
            _hasFrame = false;
            RefreshImage();
        }

        public void SetStatus(string text)
        {
            if (_status.text != text) _status.text = text;
        }

        /// <summary>
        /// Aviso en el centro de la ventana (por qué no hay imagen y qué hacer), en blanco sobre un panel oscuro.
        /// null lo oculta. Con aviso, la imagen que quede detrás se oscurece para que no parezca en vivo.
        /// </summary>
        public void SetNotice(MirrorNotice notice)
        {
            if (ReferenceEquals(notice, _notice) || (notice != null && _notice != null &&
                notice.Title == _notice.Title && notice.Hint == _notice.Hint)) return;
            _notice = notice;
            _noticePanel.SetActive(notice != null);
            if (notice != null)
            {
                _noticeTitle.text = notice.Title;
                _noticeHint.text = notice.Hint;
                _noticeHint.gameObject.SetActive(notice.Hint.Length > 0);
            }
            _image.color = notice != null ? DimmedImage : Color.white;
        }

        private void Awake() => BuildHierarchy();

        private void OnDestroy()
        {
            if (_texture != null) Destroy(_texture);
        }

        private void EnsureTexture(int width, int height)
        {
            if (_texture != null && _texture.width == width && _texture.height == height) return;
            if (_texture != null) Destroy(_texture);
            _texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                name = "HeadsetMirror",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _image.texture = _texture;
            ApplyLayout();
        }

        private void ApplyLayout()
        {
            if (_image == null) return;
            bool oneEye = _layout == MirrorLayout.LeftEye;
            _image.uvRect = oneEye ? new Rect(0f, 0f, 0.5f, 1f) : new Rect(0f, 0f, 1f, 1f);
            float width = _texture != null ? _texture.width : 2f;
            float height = _texture != null ? _texture.height : 1f;
            _fitter.aspectRatio = (oneEye ? width * 0.5f : width) / height;
        }

        private void RefreshImage()
        {
            if (_image != null) _image.enabled = _visible && _hasFrame;
        }

        private void BuildHierarchy()
        {
            var canvasObject = new GameObject("[HeadsetMirrorCanvas]", typeof(Canvas), typeof(CanvasScaler));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CanvasSortingOrder;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.matchWidthOrHeight = 0.5f;

            var imageObject = new GameObject("Feed", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
            imageObject.transform.SetParent(canvasObject.transform, false);
            Stretch((RectTransform)imageObject.transform);
            _image = imageObject.GetComponent<RawImage>();
            _image.raycastTarget = false;
            _image.color = Color.white;
            _fitter = imageObject.GetComponent<AspectRatioFitter>();
            _fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;

            var statusObject = new GameObject("Status", typeof(RectTransform), typeof(Text), typeof(Shadow));
            statusObject.transform.SetParent(canvasObject.transform, false);
            var rect = (RectTransform)statusObject.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-12f, 8f);
            rect.sizeDelta = new Vector2(1100f, 28f);
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _status = statusObject.GetComponent<Text>();
            _status.font = font;
            _status.fontSize = StatusFontSize;
            _status.alignment = TextAnchor.LowerRight;
            _status.color = new Color(1f, 1f, 1f, 0.9f);
            _status.raycastTarget = false;
            _status.horizontalOverflow = HorizontalWrapMode.Overflow;
            statusObject.GetComponent<Shadow>().effectDistance = new Vector2(1.5f, -1.5f);

            BuildNoticePanel(canvasObject.transform, font);
            ApplyLayout();
            RefreshImage();
        }

        // Panel centrado de ancho fijo: el alto se ajusta al texto (VerticalLayoutGroup + ContentSizeFitter).
        private void BuildNoticePanel(Transform canvas, Font font)
        {
            _noticePanel = new GameObject("Notice", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            _noticePanel.transform.SetParent(canvas, false);
            var rect = (RectTransform)_noticePanel.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(NoticeWidth, 0f);

            var background = _noticePanel.GetComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.72f);
            background.raycastTarget = false;

            var layout = _noticePanel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(48, 48, 30, 34);
            layout.spacing = 14f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = _noticePanel.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _noticeTitle = NoticeText("Title", font, NoticeTitleFontSize, FontStyle.Bold, Color.white);
            _noticeHint = NoticeText("Hint", font, NoticeHintFontSize, FontStyle.Normal, new Color(1f, 1f, 1f, 0.88f));
            _noticePanel.SetActive(false);
        }

        private Text NoticeText(string name, Font font, int size, FontStyle style, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(_noticePanel.transform, false);
            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.lineSpacing = 1.1f;
            text.raycastTarget = false;
            return text;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }
    }
}
