using System;
using System.Drawing;
using AcTools.Render.Base.PostEffects;
using AcTools.Render.Base.TargetTextures;
using AcTools.Utils.Helpers;
using SlimDX.Direct3D11;

namespace AcTools.Render.Kn5SpecificForwardDark {
    public partial class DarkKn5ObjectRenderer {
        private float _brandStripBlurTop = 1f;

        /// <summary>Top of the full-width glass strip, normalized to the final frame. 1 disables it.</summary>
        public float BrandStripBlurTop {
            get => _brandStripBlurTop;
            set {
                var top = float.IsNaN(value) || float.IsInfinity(value) ? 1f : Math.Max(0f, Math.Min(1f, value));
                if (_brandStripBlurTop == top) return;
                _brandStripBlurTop = top;
                IsDirty = true;
            }
        }

        private TargetResourceTexture _brandStripScene, _brandStripTemporary, _brandStripBlurred;
        private RasterizerState _brandStripScissor;

        // Call once at the END of DrawOverride(), after every DOF/AA/color-grading path.
        // Only the final 3D pixels are changed. Foreground WPF text/cards are another HWND.
        private void ApplyBrandStripBlur() {
            if (_brandStripBlurTop >= 1f || ShotKeepHdr) return;
            var target = RenderTargetView;
            var frame = target?.Resource as Texture2D;
            if (frame == null) return;
            var description = frame.Description;
            var width = description.Width;
            var height = description.Height;
            var top = Math.Max(0, Math.Min(height, (int)Math.Floor(height * _brandStripBlurTop)));
            if (width < 2 || height < 2 || top >= height) return;

            if (_brandStripScene == null || _brandStripScene.Format != description.Format) {
                DisposeBrandStripBlur();
                _brandStripScene = TargetResourceTexture.Create(description.Format);
                _brandStripTemporary = TargetResourceTexture.Create(description.Format);
                _brandStripBlurred = TargetResourceTexture.Create(description.Format);
            }
            _brandStripScene.Resize(DeviceContextHolder, width, height, null);
            _brandStripTemporary.Resize(DeviceContextHolder, Math.Max(1, width / 2), Math.Max(1, height / 2), null);
            _brandStripBlurred.Resize(DeviceContextHolder, Math.Max(1, width / 2), Math.Max(1, height / 2), null);
            if (_brandStripScissor == null) {
                _brandStripScissor = RasterizerState.FromDescription(Device, new RasterizerStateDescription {
                    FillMode = FillMode.Solid,
                    CullMode = CullMode.None,
                    IsDepthClipEnabled = false,
                    IsScissorEnabled = true
                });
            }

            var rasterizer = DeviceContext.Rasterizer;
            var oldRasterizer = rasterizer.State;
            var oldViewports = rasterizer.GetViewports();
            var oldScissors = rasterizer.GetScissorRectangles();
            var oldBlend = DeviceContext.OutputMerger.BlendState;
            var oldDepth = DeviceContext.OutputMerger.DepthStencilState;
            try {
                DeviceContext.OutputMerger.SetTargets((RenderTargetView)null);
                if (description.SampleDescription.Count > 1) {
                    DeviceContext.ResolveSubresource(frame, 0, _brandStripScene.Texture, 0, description.Format);
                } else {
                    DeviceContext.CopyResource(frame, _brandStripScene.Texture);
                }

                rasterizer.State = DeviceContextHolder.States.DoubleSidedState;
                DeviceContext.OutputMerger.BlendState = null;
                DeviceContext.OutputMerger.DepthStencilState = null;
                DeviceContextHolder.GetHelper<BlurHelper>().Blur(DeviceContextHolder,
                        _brandStripScene, _brandStripTemporary, 0.7f, 1, _brandStripBlurred);

                rasterizer.SetViewports(new Viewport(0, 0, width, height, 0f, 1f));
                rasterizer.State = _brandStripScissor;
                rasterizer.SetScissorRectangles(new Rectangle(0, top, width, height - top));
                DeviceContextHolder.GetHelper<CopyHelper>().Draw(DeviceContextHolder, _brandStripBlurred.View, target);
            } finally {
                // The next normal frame and renderer sprites must see the original state.
                DeviceContext.OutputMerger.SetTargets(target);
                DeviceContext.OutputMerger.BlendState = oldBlend;
                DeviceContext.OutputMerger.DepthStencilState = oldDepth;
                rasterizer.State = oldRasterizer;
                rasterizer.SetViewports(oldViewports);
                rasterizer.SetScissorRectangles(oldScissors);
            }
        }

        // Call from DisposeOverride(); resources also rebuild when the final frame format changes.
        private void DisposeBrandStripBlur() {
            DisposeHelper.Dispose(ref _brandStripScene);
            DisposeHelper.Dispose(ref _brandStripTemporary);
            DisposeHelper.Dispose(ref _brandStripBlurred);
            DisposeHelper.Dispose(ref _brandStripScissor);
        }
    }
}
