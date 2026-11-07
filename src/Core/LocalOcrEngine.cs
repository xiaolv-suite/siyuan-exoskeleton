using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace XiaoLv.Desktop.Core
{
    public static class LocalOcrEngine
    {
        private static bool? _isSupported = null;

        public static bool IsSupported()
        {
            if (_isSupported.HasValue) return _isSupported.Value;
            try
            {
                var ocrEngineType = Type.GetType("Windows.Media.Ocr.OcrEngine, Windows.Media, ContentType=WindowsRuntime");
                _isSupported = (ocrEngineType != null);
            }
            catch
            {
                _isSupported = false;
            }
            return _isSupported.Value;
        }

        public static string RecognizeBitmap(Bitmap bmp)
        {
            if (bmp == null || !IsSupported()) return "";

            string tempImg = Path.Combine(Path.GetTempPath(), "xiaolv_ocr_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                bmp.Save(tempImg, ImageFormat.Png);
                return RecognizeFile(tempImg);
            }
            finally
            {
                try { if (File.Exists(tempImg)) File.Delete(tempImg); } catch { }
            }
        }

        public static string RecognizeFile(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return "";

            try
            {
                var storageFileType = Type.GetType("Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime");
                var bitmapDecoderType = Type.GetType("Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType=WindowsRuntime");
                var softwareBitmapType = Type.GetType("Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics, ContentType=WindowsRuntime");
                var ocrEngineType = Type.GetType("Windows.Media.Ocr.OcrEngine, Windows.Media, ContentType=WindowsRuntime");
                var ocrResultType = Type.GetType("Windows.Media.Ocr.OcrResult, Windows.Media, ContentType=WindowsRuntime");
                var fileAccessModeType = Type.GetType("Windows.Storage.FileAccessMode, Windows.Storage, ContentType=WindowsRuntime");
                var streamType = Type.GetType("Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime");

                if (storageFileType == null || ocrEngineType == null) return "";

                // 1. StorageFile.GetFileFromPathAsync(path)
                var getFileMethod = storageFileType.GetMethod("GetFileFromPathAsync", new Type[] { typeof(string) });
                var fileOp = getFileMethod.Invoke(null, new object[] { imagePath });
                var storageFile = AwaitWinRT(fileOp, storageFileType);

                // 2. file.OpenAsync(FileAccessMode.Read)
                var openAsyncMethod = storageFileType.GetMethod("OpenAsync", new Type[] { fileAccessModeType });
                var readMode = Enum.ToObject(fileAccessModeType, 0); // Read = 0
                var streamOp = openAsyncMethod.Invoke(storageFile, new object[] { readMode });
                var stream = AwaitWinRT(streamOp, streamType);

                // 3. BitmapDecoder.CreateAsync(stream)
                var createDecoderMethod = bitmapDecoderType.GetMethod("CreateAsync", new Type[] { streamType });
                var decoderOp = createDecoderMethod.Invoke(null, new object[] { stream });
                var decoder = AwaitWinRT(decoderOp, bitmapDecoderType);

                // 4. decoder.GetSoftwareBitmapAsync()
                var getBmpMethod = bitmapDecoderType.GetMethod("GetSoftwareBitmapAsync", Type.EmptyTypes);
                var bmpOp = getBmpMethod.Invoke(decoder, null);
                var softwareBmp = AwaitWinRT(bmpOp, softwareBitmapType);

                // 5. OcrEngine.TryCreateFromUserProfileLanguages()
                var createEngineMethod = ocrEngineType.GetMethod("TryCreateFromUserProfileLanguages", Type.EmptyTypes);
                var engine = createEngineMethod.Invoke(null, null);
                if (engine == null) return "";

                // 6. engine.RecognizeAsync(softwareBmp)
                var recognizeMethod = ocrEngineType.GetMethod("RecognizeAsync", new Type[] { softwareBitmapType });
                var ocrOp = recognizeMethod.Invoke(engine, new object[] { softwareBmp });
                var result = AwaitWinRT(ocrOp, ocrResultType);

                if (result == null) return "";

                var textProp = ocrResultType.GetProperty("Text");
                var rawText = (string)textProp.GetValue(result, null);
                return CleanOcrText(rawText);
            }
            catch
            {
                return "";
            }
        }

        private static string CleanOcrText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new System.Text.StringBuilder();
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                // 去除中文之间的空格 (Windows OCR 默认可能在汉字间插入空格)
                line = System.Text.RegularExpressions.Regex.Replace(line, "([\\u4e00-\\u9fa5])\\s+([\\u4e00-\\u9fa5])", "$1$2");
                line = System.Text.RegularExpressions.Regex.Replace(line, "([\\u4e00-\\u9fa5])\\s+([\\u4e00-\\u9fa5])", "$1$2");
                sb.AppendLine(line);
            }
            return sb.ToString().TrimEnd();
        }

        private static object AwaitWinRT(object asyncOp, Type resultType)
        {
            if (asyncOp == null || resultType == null) return null;
            try
            {
                var extType = typeof(System.WindowsRuntimeSystemExtensions);
                var methods = extType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                foreach (var m in methods)
                {
                    if (m.Name == "AsTask" && m.IsGenericMethod && m.GetParameters().Length == 1)
                    {
                        var genericMethod = m.MakeGenericMethod(resultType);
                        dynamic task = genericMethod.Invoke(null, new object[] { asyncOp });
                        task.Wait();
                        return task.Result;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
