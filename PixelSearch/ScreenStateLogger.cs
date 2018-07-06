﻿using System;
using System.CodeDom;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using PixelSearch;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

public class PixelLibrary
{
	private byte[] _previousScreen;
	private bool _run, _init;

	public int Size { get; private set; }

	public int x = 0;
	public int y = 0;
	public int widthCustom = 0;
	public int heightCustom = 0;
	public Color colorCustom;
	public int step = 0;
	public int maxvariance = 0;
	public int maxThreads = 0;

	public bool isPixelSearcherActivated = false;
	public bool printTimeTaken = false;

	public void TogglePixelSearcher(bool toggle, bool printDebugInfo = false)
	{
		isPixelSearcherActivated = toggle;
		printTimeTaken = printDebugInfo;
	}

	public PixelLibrary(int x, int y, Color color, int width = 2560, int height = 1440, int step = 1, int maxvariance = 0, int threadCount = 1)
	{
		this.x = x;
		this.y = y;
		this.widthCustom = width;
		this.heightCustom = height;
		this.colorCustom = color;
		this.step = step;
		this.maxvariance = maxvariance;
		this.maxThreads = threadCount;
	}

	public void Start()
	{
		_run = true;
		var factory = new Factory1();
		//Get first adapter
		var adapter = factory.GetAdapter1(0);
		//Get device from adapter
		var device = new SharpDX.Direct3D11.Device(adapter);
		//Get front buffer of the adapter
		var output = adapter.GetOutput(0);
		var output1 = output.QueryInterface<Output1>();

		// Width/Height of desktop to capture
		int width = 0;
		int height = 0;

		if (widthCustom > output.Description.DesktopBounds.Right || widthCustom < 1)
		{
			width = output.Description.DesktopBounds.Right;
			Debug.WriteLine("Invalid Width Specified - Set to Default");
		}
		else
		{
			width = widthCustom;
		}


		if (heightCustom > output.Description.DesktopBounds.Bottom || heightCustom < 1)
		{
			height = output.Description.DesktopBounds.Bottom;
			Debug.WriteLine("Invalid Height Specified - Set to Default");
		}
		else
		{
			height = heightCustom;
		}

		// Create Staging texture CPU-accessible
		var textureDesc = new Texture2DDescription
		{
			CpuAccessFlags = CpuAccessFlags.Read,
			BindFlags = BindFlags.None,
			Format = Format.B8G8R8A8_UNorm,
			Width = output.Description.DesktopBounds.Right,
			Height = output.Description.DesktopBounds.Bottom,
			OptionFlags = ResourceOptionFlags.None,
			MipLevels = 1,
			ArraySize = 1,
			SampleDescription = { Count = 1, Quality = 0 },
			Usage = ResourceUsage.Staging
		};
		var screenTexture = new Texture2D(device, textureDesc);

		Task.Factory.StartNew(() =>
		{
			// Duplicate the output
			using (var duplicatedOutput = output1.DuplicateOutput(device))
			{
				while (_run)
				{
					try
					{
						SharpDX.DXGI.Resource screenResource;
						OutputDuplicateFrameInformation duplicateFrameInformation;

						// Try to get duplicated frame within given time is ms
						duplicatedOutput.AcquireNextFrame(5, out duplicateFrameInformation, out screenResource);

						// copy resource into memory that can be accessed by the CPU
						using (var screenTexture2D = screenResource.QueryInterface<Texture2D>())
							device.ImmediateContext.CopyResource(screenTexture2D, screenTexture);

						// Get the desktop capture texture
						var mapSource = device.ImmediateContext.MapSubresource(screenTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);

						// Create Drawing.Bitmap
						using (var bitmap = new Bitmap(widthCustom, heightCustom, PixelFormat.Format32bppArgb))
						{
							var boundsRect = new Rectangle(0,0, widthCustom, heightCustom);

							// Copy pixels from screen capture Texture to GDI bitmap
							var mapDest = bitmap.LockBits(boundsRect, ImageLockMode.ReadWrite, bitmap.PixelFormat);
							var sourcePtr = mapSource.DataPointer;
							var destPtr = mapDest.Scan0;
							for (int y = 0; y < heightCustom; y++)
							{
								// Copy a single line 
								Utilities.CopyMemory(destPtr, sourcePtr, widthCustom * 4);

								// Advance pointers
								sourcePtr = IntPtr.Add(sourcePtr, mapSource.RowPitch);
								destPtr = IntPtr.Add(destPtr, mapDest.Stride);
							}

							if (isPixelSearcherActivated)
							{
								Point p = PixelSearchThreaded(mapDest, step, maxvariance, printTimeTaken, maxThreads);
								if (p.X != -1 && p.Y != -1)
								{
									PixelFound?.Invoke(this, p);
								}
							}
							
							// Release source and dest locks
							bitmap.UnlockBits(mapDest);
							device.ImmediateContext.UnmapSubresource(screenTexture, 0);
							
							using (var ms = new MemoryStream())
							{
								bitmap.Save(ms, ImageFormat.Bmp);
								ScreenRefreshed?.Invoke(this, ms.ToArray());
								_init = true;
							}
							
						}
						screenResource.Dispose();
						duplicatedOutput.ReleaseFrame();
					}
					catch (SharpDXException e)
					{
						if (e.ResultCode.Code != SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
						{
							Trace.TraceError(e.Message);
							Trace.TraceError(e.StackTrace);
						}
					}
				}
			}
		});
		while (!_init) ;
	}

	#region Imaging Tools
	public BitmapData CropBitmap(BitmapData origBmpData, int cropX, int cropY, int cropWidth, int cropHeight)
	{
		BitmapData rawOriginal = origBmpData;

		int origByteCount = rawOriginal.Stride * rawOriginal.Height;
		byte[] origBytes = new Byte[origByteCount];
		Marshal.Copy(rawOriginal.Scan0, origBytes, 0, origByteCount);

		//I want to crop a (cropWidth*cropHeight) section starting at cropX, cropY.
		int startX = cropX;
		int startY = cropY;
		int width = cropWidth;
		int height = cropHeight;
		int BPP = 4;        //4 Bpp = 32 bits, 3 = 24, etc.

		byte[] croppedBytes = new Byte[width * height * BPP];

		//Iterate the selected area of the original image, and the full area of the new image
		for (int i = 0; i < height; i++)
		{
			for (int j = 0; j < width * BPP; j += BPP)
			{
				int origIndex = (startX * rawOriginal.Stride) + (i * rawOriginal.Stride) + (startY * BPP) + (j);
				int croppedIndex = (i * width * BPP) + (j);

				//copy data: once for each channel
				for (int k = 0; k < BPP; k++)
				{
					croppedBytes[croppedIndex + k] = origBytes[origIndex + k];
				}
			}
		}

		//copy new data into a bitmap
		Bitmap croppedBitmap = new Bitmap(width, height);
		BitmapData croppedData = croppedBitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
		Marshal.Copy(croppedBytes, 0, croppedData.Scan0, croppedBytes.Length);

		croppedBitmap.UnlockBits(croppedData);

		return croppedData;
	}
	bool ColorsAreClose(Color a, Color z, int threshold = 50)
	{
		int r = (int)a.R - z.R,
			g = (int)a.G - z.G,
			b = (int)a.B - z.B;
		return (r * r + g * g + b * b) <= threshold * threshold;
	}
	#endregion

	#region PixelSearch Related Functions
	public Point PixelSearch(BitmapData bmpData, int step = 1, int variance = 0, bool debugInfo = false)
	{
		if (bmpData != null)
		{
			int refX = x;
			int refY = y;
			if (step < 1) step = 1;

			Stopwatch ts = new Stopwatch();
			ts.Start();

			unsafe
			{
				int bytesPerPixel = Image.GetPixelFormatSize(bmpData.PixelFormat) / 8;
				int heightInPixels = bmpData.Height;
				int widthInBytes = bmpData.Width * bytesPerPixel;
				byte* ptrFirstPixel = (byte*)bmpData.Scan0;

				for (int y = 0; y < heightInPixels; y++)
				{
					byte* currentLine = ptrFirstPixel + (y * bmpData.Stride);
					for (int x = 0; x < widthInBytes; x = x + (bytesPerPixel * step))
					{
						int currentBlue = currentLine[x];
						int currentGreen = currentLine[x + 1];
						int currentRed = currentLine[x + 2];
						int currentAlpha = currentLine[x + 3];

						if (colorCustom.R == currentRed && colorCustom.G == currentGreen && colorCustom.B == currentBlue) // Not checking if matching alpha
						{
							double milliseconds = ((double)ts.ElapsedTicks / Stopwatch.Frequency) * 1000;

							// Translate location of found pixel from bitmap data to screen location
							// using our passed on reference locations (refX and refY)
							//int translatedX = refX + (x / bytesPerPixel);
							//int translatedY = refY + y;

							int translatedX = (x / bytesPerPixel);
							int translatedY = y;


							// Print time taken, will be removed from the code when 
							if (debugInfo) Debug.WriteLine("Time Taken: " + milliseconds + " ms");
							return new Point(translatedX, translatedY);
						}

						if (variance > 0)
						{
							if (ColorsAreClose(Color.FromArgb(currentRed, currentGreen, currentBlue), colorCustom, variance))
							{
								int translatedX = (x / bytesPerPixel);
								int translatedY = y;

								// Print time taken, will be removed from the code when 
								return new Point(translatedX, translatedY);
							}
						}
					}
				}
			}

			// Nothing Found
			return new Point(-1, -1);
		}

		// 'bmpData' is null
		return new Point(-1, -1);
	}

	public Point PixelSearchThreaded(BitmapData bmpData, int step = 1, int variance = 0, bool debugInfo = false, int threadCount = 8)
	{
		if (threadCount > Environment.ProcessorCount) threadCount = Environment.ProcessorCount;
	
		// if a thread count thats either 0 or 1 is set, just set it to use the single threaded version
		if (threadCount < 1 || threadCount == 1) return PixelSearch(bmpData);

		if (bmpData != null)
		{
			unsafe
			{
				int refX = x;
				int refY = y;

				int bytesPerPixel = Image.GetPixelFormatSize(bmpData.PixelFormat) / 8;
				int heightInPixels = bmpData.Height;
				int widthInBytes = bmpData.Width * bytesPerPixel;
				byte* ptrFirstPixel = (byte*) bmpData.Scan0;

				var options = new ParallelOptions();
				options.MaxDegreeOfParallelism = Environment.ProcessorCount;

				ConcurrentBag<Point> results = new ConcurrentBag<Point>();

				Parallel.For(0, heightInPixels, options, (y, state) => 
				{
					if (results.Count > 0) state.Stop();

					byte* currentLine = ptrFirstPixel + (y * bmpData.Stride);

					for (int x = 0; x < widthInBytes; x = x + (bytesPerPixel * step))
					{
						if (results.Count > 0) state.Stop();

						int currentBlue = currentLine[x];
						int currentGreen = currentLine[x + 1];
						int currentRed = currentLine[x + 2];
						int currentAlpha = currentLine[x + 3];

						if (colorCustom.R == currentRed && colorCustom.G == currentGreen && colorCustom.B == currentBlue) // Not checking if matching alpha
						{
							int translatedX = (x / bytesPerPixel);
							int translatedY = y;

							results.Add(new Point(translatedX, translatedY));
							state.Stop();
						}

						if (variance > 0)
						{
							if (ColorsAreClose(Color.FromArgb(currentRed, currentGreen, currentBlue), colorCustom, variance))
							{
								int translatedX = (x / bytesPerPixel);
								int translatedY = y;

								// Print time taken, will be removed from the code when 
								results.Add(new Point(translatedX, translatedY));
								state.Stop();
							}
						}

					}
				});


				if (results.Count > 0)
				{
					Point result;
					if (results.TryPeek(out result))
					{
						return result;
					}
				}
			}
		}

		// 'bmpData' is null
		return new Point(-1, -1);
	}

	public List<T[,]> Split2DArrayIntoChunks<T>(T[,] bigArray, int chunkCount = 2)
	{
		// for use on colum major 2d arrays

		if (bigArray.Length < 1) return new List<T[,]>();

		List<T[,]> chunkContainer = new List<T[,]>();
		int atCurrentHeight = 0;
		int heightPerChunk = bigArray.GetLength(0) / chunkCount;

		for (int threadBlock = 0; threadBlock < chunkCount; threadBlock++)
		{
			if (threadBlock == chunkCount - 1) // we're at the last chunk
			{
				int remainingLines = (bigArray.GetLength(0) - atCurrentHeight);
				Debug.WriteLine("At last chunk, we have " + remainingLines + " lines left to assign the last block!");
				T[,] newThreadBlock = new T[remainingLines, bigArray.GetLength(1)];

				for (int i = 0; i < remainingLines; i++)
				{
					for (int j = 0; j < bigArray.GetLength(1); j++)
					{
						newThreadBlock[i, j] = bigArray[atCurrentHeight + i, j];
					}
				}

				atCurrentHeight += remainingLines;
				chunkContainer.Add(newThreadBlock);
			}
			else
			{
				T[,] newThreadBlock = new T[heightPerChunk, bigArray.GetLength(1)];

				for (int i = 0; i < heightPerChunk; i++)
				{
					for (int j = 0; j < bigArray.GetLength(1); j++)
					{
						newThreadBlock[i, j] = bigArray[atCurrentHeight + i, j];
					}
				}

				atCurrentHeight += heightPerChunk;
				chunkContainer.Add(newThreadBlock);
			}
		}

		Debug.WriteLine("Blocks Available: " + chunkContainer.Count);
		Debug.WriteLine("Height Itterated Through: " + atCurrentHeight);
		Debug.WriteLine("Height Remaining: " + (bigArray.GetLength(0) - atCurrentHeight)); // if bigger than 0 something went wrong

		return chunkContainer;
	}

	public class Chunk
	{
		public byte[,] Chunk2D;
		public int RefIndex;
	}

	public List<Chunk> Chunkify2DArray(byte[,] bigArray, int chunkCount = 2)
	{
		if (bigArray.Length < 1) return new List<Chunk>();

		List<Chunk> chunkContainer = new List<Chunk>();
		int atCurrentHeight = 0;
		int heightPerChunk = bigArray.GetLength(0) / chunkCount;

		for (int threadBlock = 0; threadBlock < chunkCount; threadBlock++)
		{
			if (threadBlock == chunkCount - 1) // we're at the last chunk
			{
				Chunk newChunk = new Chunk();
				int remainingLines = (bigArray.GetLength(0) - atCurrentHeight);
				Debug.WriteLine("At last chunk, we have " + remainingLines + " lines left to assign the last block!");
				byte[,] newThreadBlock = new byte[remainingLines, bigArray.GetLength(1)];

				for (int i = 0; i < remainingLines; i++)
				{
					for (int j = 0; j < bigArray.GetLength(1); j++)
					{
						newThreadBlock[i, j] = bigArray[atCurrentHeight + i, j];
					}
				}

				newChunk.Chunk2D = newThreadBlock;
				newChunk.RefIndex = threadBlock;

				atCurrentHeight += remainingLines;
				chunkContainer.Add(newChunk);
			}
			else
			{
				byte[,] newThreadBlock = new byte[heightPerChunk, bigArray.GetLength(1)];
				Chunk newChunk = new Chunk();

				for (int i = 0; i < heightPerChunk; i++)
				{
					for (int j = 0; j < bigArray.GetLength(1); j++)
					{
						newThreadBlock[i, j] = bigArray[atCurrentHeight + i, j];
					}
				}

				newChunk.Chunk2D = newThreadBlock;
				newChunk.RefIndex = threadBlock;
				atCurrentHeight += heightPerChunk;
				chunkContainer.Add(newChunk);
			}
		}
		return chunkContainer;
	}

	private static byte[,] ConvertBufferTo2DArray(byte[] input, int height, int width)
	{
		byte[,] output = new byte[height, width];
		for (int i = 0; i < height; i++)
		{
			for (int j = 0; j < width; j++)
			{
				output[i, j] = input[i * width + j];
			}
		}
		return output;
	}
	#endregion

	#region Image Search Related Functions
	public List<Point> FindImageInImage(Bitmap sourceBitmap, Bitmap searchingBitmap)
	{
		#region Arguments check

		if (sourceBitmap == null || searchingBitmap == null)
		{
			MessageBox.Show(
				"Passed bitmaps to function is null/empty" + Environment.NewLine + Environment.NewLine + "Is SourceBitmap Null: " +
				(sourceBitmap == null).ToString() + Environment.NewLine + "Is SearchingBitmap Null: " +
				(searchingBitmap == null).ToString(), "Error Encountered!", MessageBoxButtons.OK, MessageBoxIcon.Error);

			return null;
		}

		if (sourceBitmap.PixelFormat != searchingBitmap.PixelFormat)
		{
			MessageBox.Show("The pixelformat for both images does not match!", "Error Encountered!", MessageBoxButtons.OK,
				MessageBoxIcon.Error);
			return null;
		}


		if (sourceBitmap.Width < searchingBitmap.Width || sourceBitmap.Height < searchingBitmap.Height)
		{
			MessageBox.Show("Assigned 'searchingBitmap' is bigger in size than the 'sourceBitmap!", "Error Encountered!",
				MessageBoxButtons.OK, MessageBoxIcon.Error);

			return null;
		}
			

		#endregion

		var pixelFormatSize = Image.GetPixelFormatSize(sourceBitmap.PixelFormat) / 8;

		// Copy sourceBitmap to byte array
		var sourceBitmapData = sourceBitmap.LockBits(new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height),
			ImageLockMode.ReadOnly, sourceBitmap.PixelFormat);
		var sourceBitmapBytesLength = sourceBitmapData.Stride * sourceBitmap.Height;
		var sourceBytes = new byte[sourceBitmapBytesLength];
		Marshal.Copy(sourceBitmapData.Scan0, sourceBytes, 0, sourceBitmapBytesLength);
		sourceBitmap.UnlockBits(sourceBitmapData);

		// Copy serchingBitmap to byte array
		var serchingBitmapData =
			searchingBitmap.LockBits(new Rectangle(0, 0, searchingBitmap.Width, searchingBitmap.Height),
				ImageLockMode.ReadOnly, searchingBitmap.PixelFormat);
		var serchingBitmapBytesLength = serchingBitmapData.Stride * searchingBitmap.Height;
		var serchingBytes = new byte[serchingBitmapBytesLength];
		Marshal.Copy(serchingBitmapData.Scan0, serchingBytes, 0, serchingBitmapBytesLength);
		searchingBitmap.UnlockBits(serchingBitmapData);

		var pointsList = new List<Point>();

		// Serching entries
		// minimazing serching zone
		// sourceBitmap.Height - serchingBitmap.Height + 1
		for (var mainY = 0; mainY < sourceBitmap.Height - searchingBitmap.Height + 1; mainY++)
		{
			var sourceY = mainY * sourceBitmapData.Stride;

			for (var mainX = 0; mainX < sourceBitmap.Width - searchingBitmap.Width + 1; mainX++)
			{// mainY & mainX - pixel coordinates of sourceBitmap
			 // sourceY + sourceX = pointer in array sourceBitmap bytes
				var sourceX = mainX * pixelFormatSize;

				var isEqual = true;
				for (var c = 0; c < pixelFormatSize; c++)
				{// through the bytes in pixel
					if (sourceBytes[sourceX + sourceY + c] == serchingBytes[c])
						continue;
					isEqual = false;
					break;
				}

				if (!isEqual) continue;

				var isStop = false;

				// find fist equalation and now we go deeper) 
				for (var secY = 0; secY < searchingBitmap.Height; secY++)
				{
					var serchY = secY * serchingBitmapData.Stride;

					var sourceSecY = (mainY + secY) * sourceBitmapData.Stride;

					for (var secX = 0; secX < searchingBitmap.Width; secX++)
					{// secX & secY - coordinates of serchingBitmap
					 // serchX + serchY = pointer in array serchingBitmap bytes

						var serchX = secX * pixelFormatSize;

						var sourceSecX = (mainX + secX) * pixelFormatSize;

						for (var c = 0; c < pixelFormatSize; c++)
						{// through the bytes in pixel
							if (sourceBytes[sourceSecX + sourceSecY + c] == serchingBytes[serchX + serchY + c]) continue;

							// not equal - abort iteration
							isStop = true;
							break;
						}

						if (isStop) break;
					}

					if (isStop) break;
				}

				if (!isStop)
				{// serching bitmap is founded!!
					pointsList.Add(new Point(mainX, mainY));
				}
			}
		}

		return pointsList;
	}
	public List<Point> FindImageInImage(int parrentLeft, int parrentTop, int parrentRight, int parrentBottom, Bitmap searchingBitmap)
	{
		Bitmap bmpScreenshot = new Bitmap(Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
		Graphics g = Graphics.FromImage(bmpScreenshot);
		g.CopyFromScreen(parrentLeft, parrentTop, parrentRight, parrentBottom, Screen.PrimaryScreen.Bounds.Size);

		var pixelFormatSize = Image.GetPixelFormatSize(bmpScreenshot.PixelFormat) / 8;

		// Copy sourceBitmap to byte array
		var sourceBitmapData = bmpScreenshot.LockBits(new Rectangle(0, 0, bmpScreenshot.Width, bmpScreenshot.Height),
			ImageLockMode.ReadOnly, bmpScreenshot.PixelFormat);
		var sourceBitmapBytesLength = sourceBitmapData.Stride * bmpScreenshot.Height;
		var sourceBytes = new byte[sourceBitmapBytesLength];
		Marshal.Copy(sourceBitmapData.Scan0, sourceBytes, 0, sourceBitmapBytesLength);
		bmpScreenshot.UnlockBits(sourceBitmapData);

		// Copy serchingBitmap to byte array
		var serchingBitmapData =
			searchingBitmap.LockBits(new Rectangle(0, 0, searchingBitmap.Width, searchingBitmap.Height),
				ImageLockMode.ReadOnly, searchingBitmap.PixelFormat);
		var serchingBitmapBytesLength = serchingBitmapData.Stride * searchingBitmap.Height;
		var serchingBytes = new byte[serchingBitmapBytesLength];
		Marshal.Copy(serchingBitmapData.Scan0, serchingBytes, 0, serchingBitmapBytesLength);
		searchingBitmap.UnlockBits(serchingBitmapData);

		var pointsList = new List<Point>();

		// Serching entries
		// minimazing serching zone
		// sourceBitmap.Height - serchingBitmap.Height + 1
		for (var mainY = 0; mainY < bmpScreenshot.Height - searchingBitmap.Height + 1; mainY++)
		{
			var sourceY = mainY * sourceBitmapData.Stride;

			for (var mainX = 0; mainX < bmpScreenshot.Width - searchingBitmap.Width + 1; mainX++)
			{// mainY & mainX - pixel coordinates of sourceBitmap
			 // sourceY + sourceX = pointer in array sourceBitmap bytes
				var sourceX = mainX * pixelFormatSize;

				var isEqual = true;
				for (var c = 0; c < pixelFormatSize; c++)
				{// through the bytes in pixel
					if (sourceBytes[sourceX + sourceY + c] == serchingBytes[c])
						continue;
					isEqual = false;
					break;
				}

				if (!isEqual) continue;

				var isStop = false;

				// find fist equalation and now we go deeper) 
				for (var secY = 0; secY < searchingBitmap.Height; secY++)
				{
					var serchY = secY * serchingBitmapData.Stride;

					var sourceSecY = (mainY + secY) * sourceBitmapData.Stride;

					for (var secX = 0; secX < searchingBitmap.Width; secX++)
					{// secX & secY - coordinates of serchingBitmap
					 // serchX + serchY = pointer in array serchingBitmap bytes

						var serchX = secX * pixelFormatSize;

						var sourceSecX = (mainX + secX) * pixelFormatSize;

						for (var c = 0; c < pixelFormatSize; c++)
						{// through the bytes in pixel
							if (sourceBytes[sourceSecX + sourceSecY + c] == serchingBytes[serchX + serchY + c]) continue;

							// not equal - abort iteration
							isStop = true;
							break;
						}

						if (isStop) break;
					}

					if (isStop) break;
				}

				if (!isStop)
				{// serching bitmap is founded!!
					pointsList.Add(new Point(mainX, mainY));
				}
			}
		}

		bmpScreenshot.Dispose();
		g.Dispose();
		return pointsList;
	}
	#endregion

	public void Stop()
	{
		_run = false;
	}

	public void MouseManipulate(int x, int y, bool clickAlso = false)
	{
		MouseManipulator.MouseMove(x, y);
		if (clickAlso)
		{
			MouseManipulator.MouseClickLeft();
		}
	}

	public EventHandler<byte[]> ScreenRefreshed;
	public EventHandler<Point> PixelFound;
}
 