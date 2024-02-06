using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImoutoRebirth.Navigator.ApngWpfPlayer.ApngEngine;
using ImoutoRebirth.Navigator.ApngWpfPlayer.ApngEngine.Chunks;

namespace ImoutoRebirth.Navigator.ApngWpfPlayer.ApngPlayer
{
    /// <summary>
    /// Interaction logic for ApngPlayer.xaml
    /// </summary>
    public partial class ApngPlayer : UserControl
    {
        private readonly SemaphoreSlim _loadLocker = new(1);
        private ApngImage? _apngSource;
        private CancellationTokenSource? _playingToken;
        private bool repeatPlay = false;
        private bool isSimplePng = false;
        private int endBehavoir = 0;
        private double ratio = 1.0;

        /// <summary>
        /// 开始播放 APNG 文件, 仅适用于 APNG 格式, 普通 PNG 不会触发此事件
        /// 此事件会在用户修改控件 Source 触发播放, 或重复播放时触发
        /// </summary>
        public event EventHandler<EventArgs>? PlayingStarted;
        /// <summary>
        /// 文件已经播放结束, 仅适用于 APNG 格式, 普通 PNG 不会触发此事件
        /// 此事件仅会在播放结束时触发, 重复播放不会触发此事件
        /// 更换 Source 导致的播放停止不会触发本事件
        /// </summary>
        public event EventHandler<EventArgs>? PlayingEnded;

        public ApngPlayer()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 便于直接更改 Image 参数
        /// </summary>
        public Image GetImage => Image;

        /// <summary>
        /// 重复播放
        /// </summary>
        public bool RepeatPlay
        {
            get => repeatPlay;
            set => repeatPlay = value;
        }

        /// <summary>
        /// 是否为非动态 PNG 格式, 需要载入文件后才能获取正确值
        /// </summary>
        public bool IsSimplePng
        {
            get => isSimplePng;
            set => isSimplePng = value;
        }

        /// <summary>
        /// 结束时行为, 仅会在 RepeatPlay 为 false 时生效
        /// 允许的值: 1 -> 卸载文件, 其它 -> 停止在最后一帧
        /// </summary>
        public int EndBehavoir
        {
            get => endBehavoir;
            set => endBehavoir = value;
        }

        /// <summary>
        /// 动画播放速率
        /// </summary>
        public double Ratio
        {
            get => ratio;
            set => ratio = value <= 0 ? 1.0 : value;
        }

        /// <summary>
        /// 开始播放 APNG 文件, 仅适用于 APNG 格式, 普通 PNG 不会触发此事件
        /// 此事件会在用户修改控件 Source 触发播放, 或重复播放时触发
        /// </summary>
        protected virtual async Task OnPlayingStarted(EventArgs e)
        {
            await Task.Run(() =>
            {
                PlayingStarted?.Invoke(this, e);
            });
        }
        /// <summary>
        /// 文件已经播放结束, 仅适用于 APNG 格式, 普通 PNG 不会触发此事件
        /// 此事件仅会在播放结束时触发, 重复播放不会触发此事件
        /// 更换 Source 导致的播放停止不会触发本事件
        /// </summary>
        protected virtual async Task OnPlayingEnded(EventArgs e)
        {
            await Task.Run(() =>
            {
                PlayingEnded?.Invoke(this, e);
            });
        }

        public static readonly DependencyProperty SourceProperty
            = DependencyProperty.Register(
                nameof(Source),
                typeof(string),
                typeof(ApngPlayer),
                new UIPropertyMetadata(null, OnSourceChanged));

        public string Source
        {
            get => (string)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        private static async void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var path = (string)e.NewValue;
            var control = (ApngPlayer)d;

            if (string.IsNullOrEmpty(path))
            {
                control.UnloadApng();
            }
            else
            {
                await control.ReloadApng(path);
            }
        }

        private async Task ReloadApng(string path)
        {
            await _loadLocker.WaitAsync();

            try
            {
                StopPlaying();
                _apngSource = new ApngImage(path);
                isSimplePng = _apngSource.IsSimplePng;
                if (isSimplePng)
                {
                    StartPlaying(true);
                }
                else
                {
                    _playingToken = new CancellationTokenSource();
                    StartPlaying(false, _playingToken.Token);
                }
            }
            finally
            {
                _loadLocker.Release();
            }
        }

        private void UnloadApng()
        {
            StopPlaying();
            _apngSource = null;
        }

        private async void StartPlaying(bool simple = false, CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested)
                return;

            if (_apngSource == null)
            {
                throw new InvalidOperationException();
            }

            if (simple)
            {
                Image.Source = GetBitmapImage(_apngSource);
                return;
            }

            var currentFrame = -1;
            List<WriteableBitmap> readyFrames = new();
            WriteableBitmap? writeableBitmap = null;
            while (!ct.IsCancellationRequested)
            {
                if (currentFrame == 0)
                {
                    await OnPlayingStarted(new());
                }
                currentFrame++;

                if (currentFrame >= _apngSource.Frames.Length)
                {
                    if (repeatPlay)
                    {
                        currentFrame = 0;
                    }
                    else
                    {
                        if (endBehavoir == 1)
                            UnloadApng();
                        await OnPlayingEnded(new());
                        break;
                    }
                }

                if (_apngSource.Frames.Length == 0)
                    return;
                var frame = _apngSource.Frames[currentFrame];
                if (readyFrames.Count <= currentFrame)
                {
                    var xOffset = frame.FcTlChunk.XOffset;
                    var yOffset = frame.FcTlChunk.YOffset;

                    writeableBitmap ??= BitmapFactory.New(
                        (int)_apngSource.DefaultImage.FcTlChunk.Width,
                        (int)_apngSource.DefaultImage.FcTlChunk.Height);

                    var writeableBitmapForCurrentFrame = writeableBitmap.Clone();
                    using (writeableBitmapForCurrentFrame.GetBitmapContext())
                    {
                        var frameBitmap = FromStream(frame.GetStream());

                        var blendMode = currentFrame == 0 || frame.FcTlChunk.BlendOp == BlendOps.ApngBlendOpSource
                            ? WriteableBitmapExtensions.BlendMode.None
                            : WriteableBitmapExtensions.BlendMode.Alpha;

                        if (blendMode == WriteableBitmapExtensions.BlendMode.None
                            && Math.Abs(frameBitmap.Width - writeableBitmapForCurrentFrame.Width) < 0.01
                            && Math.Abs(frameBitmap.Height - writeableBitmapForCurrentFrame.Height) < 0.01)
                        {
                            writeableBitmapForCurrentFrame = frameBitmap;
                        }
                        else
                        {
                            writeableBitmapForCurrentFrame.Blend(
                                new Point((int)xOffset, (int)yOffset),
                                frameBitmap,
                                new Rect(0, 0, frame.FcTlChunk.Width, frame.FcTlChunk.Height),
                                Colors.White,
                                blendMode);
                        }
                    }
                    readyFrames.Add(writeableBitmapForCurrentFrame.Clone());
                    readyFrames[currentFrame].Freeze();

                    switch (frame.FcTlChunk.DisposeOp)
                    {
                        case DisposeOps.ApngDisposeOpNone:
                            writeableBitmap = writeableBitmapForCurrentFrame;
                            break;
                        case DisposeOps.ApngDisposeOpPrevious:
                            // ignore change in this frame
                            break;
                        case DisposeOps.ApngDisposeOpBackground:
                            writeableBitmap = BitmapFactory.New(
                                (int)_apngSource.DefaultImage.FcTlChunk.Width,
                                (int)_apngSource.DefaultImage.FcTlChunk.Height);
                            break;
                    }

                    if (_apngSource.Frames.Length == currentFrame - 1)
                    {
                        writeableBitmap.Freeze();
                        writeableBitmap = null;
                    }
                }

                Image.Source = readyFrames[currentFrame];

                var den = frame.FcTlChunk.DelayDen == 0 ? 100 : frame.FcTlChunk.DelayDen;
                var num = frame.FcTlChunk.DelayNum;
                var delay = (int)(num * (1000.0 / den) / ratio);
                await Task.Delay(delay);
            };
        }

        private static BitmapImage GetBitmapImage(ApngImage apngSource)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = apngSource.DefaultImage.GetStream();
                bi.EndInit();
                bi.Freeze();

                return bi;
            }
            catch
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = GetUri(apngSource.SourcePath);
                bi.EndInit();
                bi.Freeze();

                return bi;
            }
        }

        private static Uri GetUri(string source)
        {
            try
            {
                return new Uri(source);
            }
            catch
            {
                var appDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return new Uri(Path.Combine(appDirectory!, source));
            };
        }

        private void StopPlaying() => _playingToken?.Cancel();

        public static WriteableBitmap FromStream(Stream stream)
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.CreateOptions = BitmapCreateOptions.None;
            source.StreamSource = stream;
            source.EndInit();
            var writeableBitmap = new WriteableBitmap(BitmapFactory.ConvertToPbgra32Format(source));
            source.UriSource = null;
            return writeableBitmap;
        }
    }
}
