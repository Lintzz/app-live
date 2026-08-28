using System;
using System.Drawing;
using System.Linq;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RadminStreamApp
{
    /// <summary>
    /// Captura de tela via Desktop Duplication API (DXGI). É a via suportada pelo Windows
    /// desde o 8: o frame já vem pronto da GPU, sem o BitBlt de tela inteira que o
    /// <see cref="VideoCapturer"/> fazia com GDI a cada quadro.
    ///
    /// Tudo aqui é best-effort: se a máquina não suportar (RDP, driver antigo, monitor
    /// híbrido), <see cref="TryCreate"/> devolve null e o capturador cai no caminho GDI.
    /// </summary>
    public sealed class DesktopDuplicationGrabber : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIOutputDuplication _duplication;
        private readonly ID3D11Texture2D _staging;

        public int Width { get; }
        public int Height { get; }

        private DesktopDuplicationGrabber(ID3D11Device device, ID3D11DeviceContext context,
            IDXGIOutputDuplication duplication, ID3D11Texture2D staging, int width, int height)
        {
            _device = device;
            _context = context;
            _duplication = duplication;
            _staging = staging;
            Width = width;
            Height = height;
        }

        /// <summary>Cria o duplicador do monitor que contém <paramref name="bounds"/>, ou null.</summary>
        public static DesktopDuplicationGrabber? TryCreate(Rectangle bounds)
        {
            IDXGIFactory1? factory = null;
            try
            {
                factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

                for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
                {
                    for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                    {
                        var desc = output.Description;
                        var rect = desc.DesktopCoordinates;
                        var outputBounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

                        if (outputBounds.Left != bounds.Left || outputBounds.Top != bounds.Top)
                        {
                            output.Dispose();
                            continue;
                        }

                        var result = D3D11.D3D11CreateDevice(
                            adapter,
                            DriverType.Unknown,
                            DeviceCreationFlags.BgraSupport,
                            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                            out ID3D11Device? device,
                            out ID3D11DeviceContext? context);

                        if (result.Failure || device == null || context == null)
                        {
                            output.Dispose();
                            continue;
                        }

                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        var duplication = output1.DuplicateOutput(device);

                        var staging = device.CreateTexture2D(new Texture2DDescription
                        {
                            Width = (uint)outputBounds.Width,
                            Height = (uint)outputBounds.Height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = Format.B8G8R8A8_UNorm,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging,
                            BindFlags = BindFlags.None,
                            CPUAccessFlags = CpuAccessFlags.Read,
                            MiscFlags = ResourceOptionFlags.None
                        });

                        output.Dispose();
                        // O adapter também sai daqui: só o device, o contexto, a duplicação e a
                        // textura seguem vivos no grabber. Antes o return pulava o Dispose lá
                        // embaixo e deixava um IDXGIAdapter1 para trás a cada duplicador criado
                        // — e um é criado a cada troca de monitor.
                        adapter.Dispose();

                        return new DesktopDuplicationGrabber(device, context, duplication, staging,
                            outputBounds.Width, outputBounds.Height);
                    }

                    adapter.Dispose();
                }
            }
            catch
            {
                // Sem duplicação disponível: quem chama usa o GDI.
            }
            finally
            {
                factory?.Dispose();
            }

            return null;
        }

        /// <summary>
        /// Copia o quadro atual para <paramref name="destination"/> (BGRA de 32 bits).
        /// Devolve false quando não houve quadro novo dentro do timeout — nesse caso o
        /// chamador reaproveita o último quadro em vez de gastar CPU.
        ///
        /// O número de linhas copiadas é limitado por <paramref name="destinationHeight"/> e
        /// pelo tamanho real do array: o monitor pode ter altura diferente da esperada pelo
        /// chamador (escala de DPI), e sem esse limite a cópia passava do fim do buffer.
        /// </summary>
        public unsafe bool TryGetFrame(byte[] destination, int destinationStride, int destinationHeight, int timeoutMs = 15)
        {
            IDXGIResource? desktopResource = null;
            bool acquired = false;

            try
            {
                var result = _duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out desktopResource);
                if (result.Failure || desktopResource == null) return false;
                acquired = true;

                // LastPresentTime zerado = só o cursor mudou; a imagem é a mesma.
                if (frameInfo.LastPresentTime == 0) return false;

                using (var texture = desktopResource.QueryInterface<ID3D11Texture2D>())
                {
                    _context.CopyResource(_staging, texture);
                }

                var map = _context.Map(_staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    int rowBytes = Math.Min((int)map.RowPitch, destinationStride);
                    int maxRowsInArray = destinationStride > 0 ? destination.Length / destinationStride : 0;
                    int rows = Math.Min(Height, Math.Min(destinationHeight, maxRowsInArray));

                    fixed (byte* dst = destination)
                    {
                        for (int y = 0; y < rows; y++)
                        {
                            Buffer.MemoryCopy(
                                (byte*)map.DataPointer + (long)y * map.RowPitch,
                                dst + (long)y * destinationStride,
                                destinationStride,
                                rowBytes);
                        }
                    }
                }
                finally
                {
                    _context.Unmap(_staging, 0);
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                desktopResource?.Dispose();
                if (acquired)
                {
                    try { _duplication.ReleaseFrame(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            try { _staging?.Dispose(); } catch { }
            try { _duplication?.Dispose(); } catch { }
            try { _context?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
        }
    }
}
