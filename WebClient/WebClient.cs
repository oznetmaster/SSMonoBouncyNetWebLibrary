//
// System.Net.WebClient
//
// Authors:
// 	Lawrence Pit (loz@cable.a2000.nl)
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//	Atsushi Enomoto (atsushi@ximian.com)
//	Miguel de Icaza (miguel@ximian.com)
//      Martin Baulig (martin.baulig@googlemail.com)
//	Marek Safar (marek.safar@gmail.com)
//
// Copyright 2003 Ximian, Inc. (http://www.ximian.com)
// Copyright 2006, 2010 Novell, Inc. (http://www.novell.com)
// Copyright 2012 Xamarin Inc. (http://www.xamarin.com)
// Copyright 2014 Microsoft Inc 
//
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
//
// Notes on CancelAsync and Async methods:
//
//    WebClient.CancelAsync is implemented by calling Thread.Interrupt
//    in our helper thread.   The various async methods have to cancel
//    any ongoing requests by calling request.Abort () at that point.
//    In a few places (UploadDataCore, UploadValuesCore,
//    UploadFileCore) we catch the ThreadInterruptedException and
//    abort the request there.
//
//    Higher level routines (the async callbacks) also need to catch
//    the exception and raise the OnXXXXCompleted events there with
//    the "canceled" flag set to true. 
//
//    In a few other places where these helper routines are not used
//    (OpenReadAsync for example) catching the ThreadAbortException
//    also must abort the request.
//
//    The Async methods currently differ in their implementation from
//    the .NET implementation in that we manually catch any other
//    exceptions and correctly raise the OnXXXXCompleted passing the
//    Exception that caused the problem.   The .NET implementation
//    does not seem to have a mechanism to flag errors that happen
//    during downloads though.    We do this because we still need to
//    catch the exception on these helper threads, or we would
//    otherwise kill the application (on the 2.x profile, uncaught
//    exceptions in threads terminate the application).
//

using System;
using System.Collections.Specialized;
using System.ComponentModel;
#if SSHARP
using Crestron.SimplSharp;
using Stream = Crestron.SimplSharp.CrestronIO.Stream;
using FileStream = Crestron.SimplSharp.CrestronIO.FileStream;
using FileMode = Crestron.SimplSharp.CrestronIO.FileMode;
using SeekOrigin = Crestron.SimplSharp.CrestronIO.SeekOrigin;
using IAsyncResult = Crestron.SimplSharp.CrestronIO.IAsyncResult;
using SSMono.IO;
using SSMono.Threading;
using SSMono.Net.Cache;
using ThreadAbortedException = System.Threading.ThreadAbortException;
#else
using System.IO;
using System.Threading;
using System.Net.Cache;
#endif
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;

#if SSHARP
namespace SSMono.Net
#else
namespace System.Net
#endif
	{
	[ComVisible (true)]
	public class WebClient : Component
		{
		private int socketBufferSize = 4096;
		internal static readonly string urlEncodedCType = "application/x-www-form-urlencoded";
		private static byte[] hexBytes;
		private ICredentials credentials;
		private WebHeaderCollection headers;
		internal WebHeaderCollection responseHeaders;
		private Uri baseAddress;
		private string baseString;
		private NameValueCollection queryString;
		internal bool is_busy;
		private bool async;
		private bool proxySet = false;
#if THREADING 
		internal Thread async_thread;
#endif
		internal Encoding encoding = Encoding.Default;
		private IWebProxy proxy;
//		RequestCachePolicy cache_policy;

		// Constructors
		static WebClient ()
			{
			hexBytes = new byte[16];
			int index = 0;
			for (int i = '0'; i <= '9'; i++, index++)
				hexBytes[index] = (byte)i;

			for (int i = 'a'; i <= 'f'; i++, index++)
				hexBytes[index] = (byte)i;
			}

		public WebClient ()
			{
			}

		// Properties

		public string BaseAddress
			{
			get
				{
				if (baseString == null)
					{
					if (baseAddress == null)
						return string.Empty;
					}

				baseString = baseAddress.ToString ();
				return baseString;
				}

			set
				{
				if (value == null || value.Length == 0)
					baseAddress = null;
				else
					baseAddress = new Uri (value);
				}
			}

		private static Exception GetMustImplement ()
			{
			return new NotImplementedException ();
			}

		public RequestCachePolicy CachePolicy
			{
			get { throw GetMustImplement (); }
			set
				{
				/*cache_policy = value;*/
				}
			}

		public bool UseDefaultCredentials
			{
			get { throw GetMustImplement (); }
			set
				{
				// This makes no sense in mono
				}
			}

		public ICredentials Credentials
			{
			get { return credentials; }
			set { credentials = value; }
			}

		public WebHeaderCollection Headers
			{
			get
				{
				if (headers == null)
					headers = new WebHeaderCollection ();

				return headers;
				}
			set { headers = value; }
			}

		public NameValueCollection QueryString
			{
			get
				{
				if (queryString == null)
					queryString = new NameValueCollection ();

				return queryString;
				}
			set { queryString = value; }
			}

		public WebHeaderCollection ResponseHeaders
			{
			get { return responseHeaders; }
			}

		public Encoding Encoding
			{
			get { return encoding; }
			set
				{
				if (value == null)
					throw new ArgumentNullException ("Encoding");
				encoding = value;
				}
			}

		public IWebProxy Proxy
			{
			get
				{
				if (!proxySet)
					return WebRequest.DefaultWebProxy;

				return proxy;
				}
			set
				{
				proxy = value;
				proxySet = true;
				}
			}

		public virtual bool IsBusy
			{
			get
				{
				return is_busy;
				}
			}

		// Methods

		internal void CheckBusy ()
			{
			if (IsBusy)
				throw new NotSupportedException ("WebClient does not support concurrent I/O operations.");
			}

		internal void SetBusy ()
			{
			lock (this)
				{
				CheckBusy ();
				is_busy = true;
				}
			}

		//   DownloadData

		public byte[] DownloadData (string address)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			return DownloadData (CreateUri (address));
			}

		public byte[] DownloadData (Uri address)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			try
				{
				SetBusy ();
				async = false;
				return DownloadDataCore (address, null);
				}
			finally
				{
				is_busy = false;
				}
			}

		private byte[] DownloadDataCore (Uri address, object userToken)
			{
			WebRequest request = null;

			try
				{
				request = SetupRequest (address);
				return ReadAll (request, userToken);
				}
#if THREADING
			catch (ThreadInterruptedException)
				{
				if (request != null)
					request.Abort ();
				throw new WebException ("User canceled the request", WebExceptionStatus.RequestCanceled);
				}
#endif
#if SSHARP
			catch (ThreadAbortedException)
				{
				if (request != null)
					request.Abort ();
				throw new WebException ("User canceled the request", WebExceptionStatus.RequestCanceled);
				}
#endif
			catch (WebException)
				{
				throw;
				}
			catch (Exception ex)
				{
				throw new WebException ("An error occurred performing a WebClient request.", ex);
				}
			}

		//   DownloadFile

		public void DownloadFile (string address, string fileName)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			DownloadFile (CreateUri (address), fileName);
			}

		public void DownloadFile (Uri address, string fileName)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (fileName == null)
				throw new ArgumentNullException ("fileName");

			try
				{
				SetBusy ();
				async = false;
				DownloadFileCore (address, fileName, null);
				}
			catch (WebException)
				{
				throw;
				}
			catch (Exception ex)
				{
				throw new WebException ("An error occurred " + "performing a WebClient request.", ex);
				}
			finally
				{
				is_busy = false;
				}
			}

		private void DownloadFileCore (Uri address, string fileName, object userToken)
			{
			WebRequest request = null;

			using (FileStream f = new FileStream (fileName, FileMode.Create))
				{
				try
					{
					request = SetupRequest (address);
					WebResponse response = GetWebResponse (request);
					Stream st = response.GetResponseStream ();

					int cLength = (int)response.ContentLength;
					int length = (cLength <= -1 || cLength > 32 * 1024) ? 32 * 1024 : cLength;
					byte[] buffer = new byte[length];

					int nread = 0;
					long notify_total = 0;
					while ((nread = st.Read (buffer, 0, length)) != 0)
						{
						notify_total += nread;
						if (async)
							OnDownloadProgressChanged (new DownloadProgressChangedEventArgs (notify_total, response.ContentLength, userToken));
						f.Write (buffer, 0, nread);
						}

					if (cLength > 0 && notify_total < cLength)
						throw new WebException ("Download aborted prematurely.", WebExceptionStatus.ReceiveFailure);
					}
#if THREADING
				catch (ThreadInterruptedException)
					{
					if (request != null)
						request.Abort ();
					throw;
					}
#endif
#if SSHARP
				catch (ThreadAbortedException)
					{
					if (request != null)
						request.Abort ();
					}
#endif
				}
			}

		//   OpenRead

		public Stream OpenRead (string address)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			return OpenRead (CreateUri (address));
			}

		public Stream OpenRead (Uri address)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			WebRequest request = null;
			try
				{
				SetBusy ();
				async = false;
				request = SetupRequest (address);
				WebResponse response = GetWebResponse (request);
				return response.GetResponseStream ();
				}
			catch (WebException)
				{
				throw;
				}
			catch (Exception ex)
				{
				throw new WebException ("An error occurred " + "performing a WebClient request.", ex);
				}
			finally
				{
				is_busy = false;
				}
			}

		//   OpenWrite

		public Stream OpenWrite (string address)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			return OpenWrite (CreateUri (address));
			}

		public Stream OpenWrite (string address, string method)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			return OpenWrite (CreateUri (address), method);
			}

		public Stream OpenWrite (Uri address)
			{
			return OpenWrite (address, (string)null);
			}

		public Stream OpenWrite (Uri address, string method)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			try
				{
				SetBusy ();
				async = false;
				WebRequest request = SetupRequest (address, method, true);
				return OpenWriteStream (request);
				}
			catch (WebException)
				{
				throw;
				}
			catch (Exception ex)
				{
				throw new WebException ("An error occurred " + "performing a WebClient request.", ex);
				}
			finally
				{
				is_busy = false;
				}
			}

		private Stream OpenWriteStream (WebRequest request)
			{
			var stream = request.GetRequestStream ();
			var wcs = stream as WebConnectionStream;
			if (wcs != null)
				wcs.GetResponseOnClose = true;
			return stream;
			}

		internal string DetermineMethod (Uri address, string method, bool is_upload)
			{
			if (method != null)
				return method;

			if (address.Scheme == Uri.UriSchemeFtp)
				return (is_upload) ? "STOR" : "RETR";

			return (is_upload) ? "POST" : "GET";
			}

		//   UploadData

		public byte[] UploadData (string address, byte[] data)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			return UploadData (CreateUri (address), data);
			}

		public byte[] UploadData (string address, string method, byte[] data)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			return UploadData (CreateUri (address), method, data);
			}

		public byte[] UploadData (Uri address, byte[] data)
			{
			return UploadData (address, (string)null, data);
			}

		public byte[] UploadData (Uri address, string method, byte[] data)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (data == null)
				throw new ArgumentNullException ("data");

			try
				{
				SetBusy ();
				async = false;
				return UploadDataCore (address, method, data, null);
				}
			catch (WebException)
				{
				throw;
				}
			catch (Exception ex)
				{
				throw new WebException ("An error occurred " + "performing a WebClient request.", ex);
				}
			finally
				{
				is_busy = false;
				}
			}

		private byte[] UploadDataCore (Uri address, string method, byte[] data, object userToken)
			{
			WebRequest request = SetupRequest (address, method, true);
			try
				{
				int contentLength = data.Length;
				request.ContentLength = contentLength;
				using (Stream stream = request.GetRequestStream ())
					{
					int offset = 0;
					while (offset < contentLength)
						{
						var size = Math.Min (contentLength - offset, socketBufferSize);
						stream.Write (data, offset, size);

						offset += size;
						int percent = 0;
						if (contentLength > 0)
							percent = (int)((long)offset * 100 / contentLength);
						var args = new UploadProgressChangedEventArgs (0, 0, offset, contentLength, percent, userToken);
						OnUploadProgressChanged (args);
						}
					}

				return ReadAll (request, userToken);
				}
#if THREADING
			catch (ThreadInterruptedException)
				{
				if (request != null)
					request.Abort ();
				throw;
				}
#endif
#if SSHARP
			catch (ThreadAbortedException)
				{
				if (request != null)
					request.Abort ();
				throw;
				}
#endif
			}

		//   UploadFile

		public byte[] UploadFile (string address, string fileName)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			return UploadFile (CreateUri (address), fileName);
			}

		public byte[] UploadFile (Uri address, string fileName)
			{
			return UploadFile (address, (string)null, fileName);
			}

		public byte[] UploadFile (string address, string method, string fileName)
			{
			return UploadFile (CreateUri (address), method, fileName);
			}

		public byte[] UploadFile (Uri address, string method, string fileName)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (fileName == null)
				throw new ArgumentNullException ("fileName");

			try
				{
				SetBusy ();
				async = false;
				return UploadFileCore (address, method, fileName, null);
				}
			catch (WebException)
				{
				throw;
				}
			catch (Exception ex)
				{
				throw new WebException ("An error occurred " + "performing a WebClient request.", ex);
				}
			finally
				{
				is_busy = false;
				}
			}

		// From the Microsoft reference source
		private string MapToDefaultMethod (Uri address)
			{
			Uri uri;
			if (!address.IsAbsoluteUri && baseAddress != null)
				uri = new Uri (baseAddress, address);
			else
				uri = address;
			if (uri.Scheme.ToLower (System.Globalization.CultureInfo.InvariantCulture) == "ftp")
				return WebRequestMethods.Ftp.UploadFile;
			else
				return "POST";
			}

		private byte[] UploadFileCore (Uri address, string method, string fileName, object userToken)
			{
			string fileCType = Headers["Content-Type"];
			if (fileCType != null)
				{
				string lower = fileCType.ToLower ();
				if (lower.StartsWith ("multipart/"))
					throw new WebException ("Content-Type cannot be set to a multipart" + " type for this request.");
				}
			else
				fileCType = "application/octet-stream";

			if (method == null)
				method = MapToDefaultMethod (address);

			bool needs_boundary = (method != "PUT" && method != WebRequestMethods.Ftp.UploadFile); // only verified case so far
			string boundary = null;
			if (needs_boundary)
				{
				boundary = "------------" + DateTime.Now.Ticks.ToString ("x");
				Headers["Content-Type"] = String.Format ("multipart/form-data; boundary={0}", boundary);
				}
			Stream reqStream = null;
			Stream fStream = null;
			byte[] resultBytes = null;

			fileName = Path.GetFullPath (fileName);

			WebRequest request = null;
			try
				{
				fStream = File.OpenRead (fileName);
				request = SetupRequest (address, method, true);
				reqStream = request.GetRequestStream ();
				byte[] bytes_boundary = null;
				if (needs_boundary)
					{
					bytes_boundary = Encoding.ASCII.GetBytes (boundary);
					reqStream.WriteByte ((byte)'-');
					reqStream.WriteByte ((byte)'-');
					reqStream.Write (bytes_boundary, 0, bytes_boundary.Length);
					reqStream.WriteByte ((byte)'\r');
					reqStream.WriteByte ((byte)'\n');
					string partHeaders = String.Format ("Content-Disposition: form-data; " + "name=\"file\"; filename=\"{0}\"\r\n" + "Content-Type: {1}\r\n\r\n",
						Path.GetFileName (fileName), fileCType);

					byte[] partHeadersBytes = Encoding.UTF8.GetBytes (partHeaders);
					reqStream.Write (partHeadersBytes, 0, partHeadersBytes.Length);
					}
				int nread;
				long bytes_sent = 0;
				long file_size = -1;
				long step = 16384; // every 16kB
				if (fStream.CanSeek)
					{
					file_size = fStream.Length;
					step = file_size / 100;
					}
				var upload_args = new UploadProgressChangedEventArgs (0, 0, bytes_sent, file_size, 0, userToken);
				OnUploadProgressChanged (upload_args);
				byte[] buffer = new byte[4096];
				long sum = 0;
				while ((nread = fStream.Read (buffer, 0, 4096)) > 0)
					{
					reqStream.Write (buffer, 0, nread);
					bytes_sent += nread;
					sum += nread;
					if (sum >= step || nread < 4096)
						{
						int percent = 0;
						if (file_size > 0)
							percent = (int)(bytes_sent * 100 / file_size);
						upload_args = new UploadProgressChangedEventArgs (0, 0, bytes_sent, file_size, percent, userToken);
						OnUploadProgressChanged (upload_args);
						sum = 0;
						}
					}

				if (needs_boundary)
					{
					reqStream.WriteByte ((byte)'\r');
					reqStream.WriteByte ((byte)'\n');
					reqStream.WriteByte ((byte)'-');
					reqStream.WriteByte ((byte)'-');
					reqStream.Write (bytes_boundary, 0, bytes_boundary.Length);
					reqStream.WriteByte ((byte)'-');
					reqStream.WriteByte ((byte)'-');
					reqStream.WriteByte ((byte)'\r');
					reqStream.WriteByte ((byte)'\n');
					}
				reqStream.Close ();
				reqStream = null;
				resultBytes = ReadAll (request, userToken);
				}
#if THREADING
			catch (ThreadInterruptedException)
				{
				if (request != null)
					request.Abort ();
				throw;
				}
#endif
#if SSHARP
			catch (ThreadAbortedException)
				{
				if (request != null)
					request.Abort ();
				}
#endif
			finally
				{
				if (fStream != null)
					fStream.Close ();

				if (reqStream != null)
					reqStream.Close ();
				}

			return resultBytes;
			}

		public byte[] UploadValues (string address, NameValueCollection data)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			return UploadValues (CreateUri (address), data);
			}

		public byte[] UploadValues (string address, string method, NameValueCollection data)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			return UploadValues (CreateUri (address), method, data);
			}

		public byte[] UploadValues (Uri address, NameValueCollection data)
			{
			return UploadValues (address, (string)null, data);
			}

		public byte[] UploadValues (Uri address, string method, NameValueCollection data)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (data == null)
				throw new ArgumentNullException ("data");

			try
				{
				SetBusy ();
				async = false;
				return UploadValuesCore (address, method, data, null);
				}
			catch (WebException)
				{
				throw;
				}
			catch (Exception ex)
				{
				throw new WebException ("An error occurred " + "performing a WebClient request.", ex);
				}
			finally
				{
				is_busy = false;
				}
			}

		private byte[] UploadValuesCore (Uri uri, string method, NameValueCollection data, object userToken)
			{
			string cType = Headers["Content-Type"];
			if (cType != null && String.Compare (cType, urlEncodedCType, true) != 0)
				throw new WebException ("Content-Type header cannot be changed from its default " + "value for this request.");

			Headers["Content-Type"] = urlEncodedCType;
			WebRequest request = SetupRequest (uri, method, true);
			try
				{
				MemoryStream tmpStream = new MemoryStream ();
				foreach (string key in data)
					{
					byte[] bytes = Encoding.UTF8.GetBytes (key);
					UrlEncodeAndWrite (tmpStream, bytes);
					tmpStream.WriteByte ((byte)'=');
					bytes = Encoding.UTF8.GetBytes (data[key]);
					UrlEncodeAndWrite (tmpStream, bytes);
					tmpStream.WriteByte ((byte)'&');
					}

				int length = (int)tmpStream.Length;
				if (length > 0)
					tmpStream.SetLength (--length); // remove trailing '&'

				byte[] buf = tmpStream.GetBuffer ();
				request.ContentLength = length;
				using (Stream rqStream = request.GetRequestStream ())
					rqStream.Write (buf, 0, length);
				tmpStream.Close ();

				return ReadAll (request, userToken);
				}
#if THREADING
			catch (ThreadInterruptedException)
				{
				request.Abort ();
				throw;
				}
#endif
#if SSHARP
			catch (ThreadAbortedException)
				{
				request.Abort ();
				throw;
				}
#endif
			}

		public string DownloadString (string address)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			return ConvertDataToString (DownloadData (CreateUri (address)));
			}

		public string DownloadString (Uri address)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			return ConvertDataToString (DownloadData (CreateUri (address)));
			}

		internal string ConvertDataToString (byte[] data)
			{
			int preambleLength = 0;
			var enc = GetEncodingFromBuffer (data, data.Length, ref preambleLength) ?? encoding;
			return enc.GetString (data, preambleLength, data.Length - preambleLength);
			}

		internal static Encoding GetEncodingFromBuffer (byte[] buffer, int length, ref int preambleLength)
			{
			var encodings_with_preamble = new[] {
			Encoding.UTF8, 
#if !SSHARP
			Encoding.UTF32, 
#endif
			Encoding.Unicode
			};
			foreach (var enc in encodings_with_preamble)
				{
				if ((preambleLength = StartsWith (buffer, length, enc.GetPreamble ())) > 0)
					return enc;
				}

			return null;
			}

		private static int StartsWith (byte[] array, int length, byte[] value)
			{
			if (length < value.Length)
				return 0;

			for (int i = 0; i < value.Length; ++i)
				{
				if (array[i] != value[i])
					return 0;
				}

			return value.Length;
			}

		public string UploadString (string address, string data)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (data == null)
				throw new ArgumentNullException ("data");

			byte[] resp = UploadData (address, encoding.GetBytes (data));
			return encoding.GetString (resp);
			}

		public string UploadString (string address, string method, string data)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (data == null)
				throw new ArgumentNullException ("data");

			byte[] resp = UploadData (address, method, encoding.GetBytes (data));
			return encoding.GetString (resp);
			}

		public string UploadString (Uri address, string data)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (data == null)
				throw new ArgumentNullException ("data");

			byte[] resp = UploadData (address, encoding.GetBytes (data));
			return encoding.GetString (resp);
			}

		public string UploadString (Uri address, string method, string data)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (data == null)
				throw new ArgumentNullException ("data");

			byte[] resp = UploadData (address, method, encoding.GetBytes (data));
			return encoding.GetString (resp);
			}

		public event DownloadDataCompletedEventHandler DownloadDataCompleted;
		public event AsyncCompletedEventHandler DownloadFileCompleted;
		public event DownloadProgressChangedEventHandler DownloadProgressChanged;
		public event DownloadStringCompletedEventHandler DownloadStringCompleted;
		public event OpenReadCompletedEventHandler OpenReadCompleted;
		public event OpenWriteCompletedEventHandler OpenWriteCompleted;
		public event UploadDataCompletedEventHandler UploadDataCompleted;
		public event UploadFileCompletedEventHandler UploadFileCompleted;
		public event UploadProgressChangedEventHandler UploadProgressChanged;
		public event UploadStringCompletedEventHandler UploadStringCompleted;
		public event UploadValuesCompletedEventHandler UploadValuesCompleted;

		internal Uri CreateUri (string address)
			{
			Uri uri;
			try
				{
				if (baseAddress == null)
					uri = new Uri (address);
				else
					uri = new Uri (baseAddress, address);
				return CreateUri (uri);
				}
			catch
				{
				}
			return new Uri (Path.GetFullPath (address));
			}

		internal Uri CreateUri (Uri address)
			{
			Uri result = address;
			if (baseAddress != null && !result.IsAbsoluteUri)
				{
				try
					{
					result = new Uri (baseAddress, result.OriginalString);
					}
				catch
					{
					return result; // Not much we can do here.
					}
				}

			string query = result.Query;
			if (String.IsNullOrEmpty (query))
				query = GetQueryString (true);
			UriBuilder builder = new UriBuilder (address);
			if (!String.IsNullOrEmpty (query))
				builder.Query = query.Substring (1);
			return builder.Uri;
			}

		internal string GetQueryString (bool add_qmark)
			{
			if (queryString == null || queryString.Count == 0)
				return null;

			StringBuilder sb = new StringBuilder ();
			if (add_qmark)
				sb.Append ('?');

			foreach (string key in queryString)
				sb.AppendFormat ("{0}={1}&", key, UrlEncode (queryString[key]));

			if (sb.Length != 0)
				sb.Length--; // removes last '&' or the '?' if empty.

			if (sb.Length == 0)
				return null;

			return sb.ToString ();
			}

		internal WebRequest SetupRequest (Uri uri)
			{
			WebRequest request = GetWebRequest (uri);
			if (proxySet)
				request.Proxy = Proxy;
			if (credentials != null)
				request.Credentials = credentials;
			else if (!String.IsNullOrEmpty (uri.UserInfo))
				{
				// Perhaps this should be done by the underlying URI handler?
				ICredentials creds = GetCredentials (uri.UserInfo);
				if (creds != null)
					request.Credentials = creds;
				}

			// Special headers. These are properties of HttpWebRequest.
			// What do we do with other requests differnt from HttpWebRequest?
			if (headers != null && headers.Count != 0 && (request is HttpWebRequest))
				{
				HttpWebRequest req = (HttpWebRequest)request;
				string expect = headers["Expect"];
				string contentType = headers["Content-Type"];
				string accept = headers["Accept"];
				string connection = headers["Connection"];
				string userAgent = headers["User-Agent"];
				string referer = headers["Referer"];
				headers.RemoveInternal ("Expect");
				headers.RemoveInternal ("Content-Type");
				headers.RemoveInternal ("Accept");
				headers.RemoveInternal ("Connection");
				headers.RemoveInternal ("Referer");
				headers.RemoveInternal ("User-Agent");
				request.Headers = headers;

				if (expect != null && expect.Length > 0)
					req.Expect = expect;

				if (accept != null && accept.Length > 0)
					req.Accept = accept;

				if (contentType != null && contentType.Length > 0)
					req.ContentType = contentType;

				if (connection != null && connection.Length > 0)
					req.Connection = connection;

				if (userAgent != null && userAgent.Length > 0)
					req.UserAgent = userAgent;

				if (referer != null && referer.Length > 0)
					req.Referer = referer;
				}

			responseHeaders = null;
			return request;
			}

		internal WebRequest SetupRequest (Uri uri, string method, bool is_upload)
			{
			WebRequest request = SetupRequest (uri);
			request.Method = DetermineMethod (uri, method, is_upload);
			return request;
			}

		private static NetworkCredential GetCredentials (string user_info)
			{
			string[] creds = user_info.Split (':');
			if (creds.Length != 2)
				return null;

			if (creds[0].IndexOf ('\\') != -1)
				{
				string[] user = creds[0].Split ('\\');
				if (user.Length != 2)
					return null;
				return new NetworkCredential (user[1], creds[1], user[0]);
				}
			return new NetworkCredential (creds[0], creds[1]);
			}

		private byte[] ReadAll (WebRequest request, object userToken)
			{
			WebResponse response = GetWebResponse (request);
			Stream stream = response.GetResponseStream ();
			int length = (int)response.ContentLength;
			HttpWebRequest wreq = request as HttpWebRequest;

			if (length > -1 && wreq != null && (int)wreq.AutomaticDecompression != 0)
				{
				string content_encoding = ((HttpWebResponse)response).ContentEncoding;
				if (((content_encoding == "gzip" && (wreq.AutomaticDecompression & DecompressionMethods.GZip) != 0))
					|| ((content_encoding == "deflate" && (wreq.AutomaticDecompression & DecompressionMethods.Deflate) != 0)))
					length = -1;
				}

			MemoryStream ms = null;
			bool nolength = (length == -1);
			int size = ((nolength) ? 8192 : length);
			if (nolength)
				ms = new MemoryStream ();

			long total = 0;
			int nread = 0;
			int offset = 0;
			byte[] buffer = new byte[size];
			while ((nread = stream.Read (buffer, offset, size)) != 0)
				{
				if (nolength)
					ms.Write (buffer, 0, nread);
				else
					{
					offset += nread;
					size -= nread;
					}
				if (async)
					{
					total += nread;
					OnDownloadProgressChanged (new DownloadProgressChangedEventArgs (total, length, userToken));
					}
				}

			if (nolength)
				return ms.ToArray ();

			return buffer;
			}

		private string UrlEncode (string str)
			{
			StringBuilder result = new StringBuilder ();

			int len = str.Length;
			for (int i = 0; i < len; i++)
				{
				char c = str[i];
				if (c == ' ')
					result.Append ('+');
				else if ((c < '0' && c != '-' && c != '.') || (c < 'A' && c > '9') || (c > 'Z' && c < 'a' && c != '_') || (c > 'z'))
					{
					result.Append ('%');
					int idx = ((int)c) >> 4;
					result.Append ((char)hexBytes[idx]);
					idx = ((int)c) & 0x0F;
					result.Append ((char)hexBytes[idx]);
					}
				else
					result.Append (c);
				}

			return result.ToString ();
			}

		internal static void UrlEncodeAndWrite (Stream stream, byte[] bytes)
			{
			if (bytes == null)
				return;

			int len = bytes.Length;
			if (len == 0)
				return;

			for (int i = 0; i < len; i++)
				{
				char c = (char)bytes[i];
				if (c == ' ')
					stream.WriteByte ((byte)'+');
				else if ((c < '0' && c != '-' && c != '.') || (c < 'A' && c > '9') || (c > 'Z' && c < 'a' && c != '_') || (c > 'z'))
					{
					stream.WriteByte ((byte)'%');
					int idx = ((int)c) >> 4;
					stream.WriteByte (hexBytes[idx]);
					idx = ((int)c) & 0x0F;
					stream.WriteByte (hexBytes[idx]);
					}
				else
					stream.WriteByte ((byte)c);
				}
			}

		public virtual void CancelAsync ()
			{
			lock (this)
				{
#if THREADING
				if (async_thread == null)
					return;
#else
				if (!async)
					return;
#endif

				//
				// We first flag things as done, in case the Interrupt hangs
				// or the thread decides to hang in some other way inside the
				// event handlers, or if we are stuck somewhere else.  This
				// ensures that the WebClient object is reusable immediately
				//
#if THREADING
				Thread t = async_thread;
#endif
				CompleteAsync ();
#if THREADING
#if SSHARP
				t.Abort ();
#else
				t.Interrupt ();
#endif
#endif
				}
			}

		internal virtual void CompleteAsync ()
			{
			lock (this)
				{
				is_busy = false;
#if THREADING
				async_thread = null;
#endif
				}
			}

		//    DownloadDataAsync

		public void DownloadDataAsync (Uri address)
			{
			DownloadDataAsync (address, null);
			}

		public void DownloadDataAsync (Uri address, object userToken)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			lock (this)
				{
				SetBusy ();
				async = true;
				object[] cb_args = new object[] {CreateUri (address), userToken};
#if THREADING
				async_thread = new Thread (delegate (object state)
					{
					object[] args = (object[])state;
					try
						{
						byte[] data = DownloadDataCore ((Uri)args[0], args[1]);
						OnDownloadDataCompleted (new DownloadDataCompletedEventArgs (data, null, false, args[1]));
						}
					catch (Exception e)
						{
						bool canceled = false;
						WebException we = e as WebException;
						if (we != null)
							canceled = we.Status == WebExceptionStatus.RequestCanceled;
						OnDownloadDataCompleted (new DownloadDataCompletedEventArgs (null, e, canceled, args[1]));
						}
					});
				async_thread.IsBackground = true;
				async_thread.Start (cb_args);
#else
				ThreadPool.QueueUserWorkItem ((state) =>
					{
					object[] args = (object[])state;
					try
						{
						byte[] data = DownloadDataCore ((Uri)args[0], args[1]);
						OnDownloadDataCompleted (new DownloadDataCompletedEventArgs (data, null, false, args[1]));
						}
					catch (Exception e)
						{
						bool canceled = false;
						WebException we = e as WebException;
						if (we != null)
							canceled = we.Status == WebExceptionStatus.RequestCanceled;
						OnDownloadDataCompleted (new DownloadDataCompletedEventArgs (null, e, canceled, args[1]));
						}
					}, cb_args);
#endif
				}
			}

		//    DownloadFileAsync

		public void DownloadFileAsync (Uri address, string fileName)
			{
			DownloadFileAsync (address, fileName, null);
			}

		public void DownloadFileAsync (Uri address, string fileName, object userToken)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (fileName == null)
				throw new ArgumentNullException ("fileName");

			lock (this)
				{
				SetBusy ();
				async = true;

				object[] cb_args = new object[] {CreateUri (address), fileName, userToken};
#if THREADING
				async_thread = new Thread (delegate (object state)
					{
					object[] args = (object[])state;
					try
						{
						DownloadFileCore ((Uri)args[0], (string)args[1], args[2]);
						OnDownloadFileCompleted (new AsyncCompletedEventArgs (null, false, args[2]));
						}
					catch (ThreadInterruptedException)
						{
						OnDownloadFileCompleted (new AsyncCompletedEventArgs (null, true, args[2]));
						}
#if SSHARP
					catch (ThreadAbortedException)
						{
						ThreadPool.QueueUserWorkItem (_ => OnDownloadFileCompleted (new AsyncCompletedEventArgs (null, true, args[2])));
						}
#endif
					catch (Exception e)
						{
						OnDownloadFileCompleted (new AsyncCompletedEventArgs (e, false, args[2]));
						}
					});
				async_thread.IsBackground = true;
				async_thread.Start (cb_args);
#else
				ThreadPool.QueueUserWorkItem ((state) =>
					{
					object[] args = (object[])state;
					try
						{
						DownloadFileCore ((Uri)args[0], (string)args[1], args[2]);
						OnDownloadFileCompleted (new AsyncCompletedEventArgs (null, false, args[2]));
						}
					catch (Exception e)
						{
						OnDownloadFileCompleted (new AsyncCompletedEventArgs (e, false, args[2]));
						}
					}, cb_args);
#endif
				}
			}

		//    DownloadStringAsync

		public void DownloadStringAsync (Uri address)
			{
			DownloadStringAsync (address, null);
			}

		public void DownloadStringAsync (Uri address, object userToken)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			lock (this)
				{
				SetBusy ();
				async = true;

				object[] cb_args = new object[] {CreateUri (address), userToken};
#if THREADING
				async_thread = new Thread (delegate (object state)
					{
					object[] args = (object[])state;
					try
						{
						string data = ConvertDataToString (DownloadDataCore ((Uri)args[0], args[1]));
						OnDownloadStringCompleted (new DownloadStringCompletedEventArgs (data, null, false, args[1]));
						}
					catch (Exception e)
						{
						bool canceled = false;
						WebException we = e as WebException;
						if (we != null)
							canceled = we.Status == WebExceptionStatus.RequestCanceled;
						OnDownloadStringCompleted (new DownloadStringCompletedEventArgs (null, e, canceled, args[1]));
						}
					});
				async_thread.IsBackground = true;
				async_thread.Start (cb_args);
#else
				ThreadPool.QueueUserWorkItem ((state) =>
					{
					object[] args = (object[])state;
					try
						{
						string data = ConvertDataToString (DownloadDataCore ((Uri)args[0], args[1]));
						OnDownloadStringCompleted (new DownloadStringCompletedEventArgs (data, null, false, args[1]));
						}
					catch (Exception e)
						{
						bool canceled = false;
						WebException we = e as WebException;
						if (we != null)
							canceled = we.Status == WebExceptionStatus.RequestCanceled;
						OnDownloadStringCompleted (new DownloadStringCompletedEventArgs (null, e, canceled, args[1]));
						}
					}, cb_args);
#endif
				}
			}

		//    OpenReadAsync

		public void OpenReadAsync (Uri address)
			{
			OpenReadAsync (address, null);
			}

		public void OpenReadAsync (Uri address, object userToken)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			lock (this)
				{
				SetBusy ();
				async = true;

				object[] cb_args = new object[] {CreateUri (address), userToken};
#if THREADING
				async_thread = new Thread (delegate (object state)
					{
					object[] args = (object[])state;
					WebRequest request = null;
					try
						{
						request = SetupRequest ((Uri)args[0]);
						WebResponse response = GetWebResponse (request);
						Stream stream = response.GetResponseStream ();
						OnOpenReadCompleted (new OpenReadCompletedEventArgs (stream, null, false, args[1]));
						}
					catch (ThreadInterruptedException)
						{
						if (request != null)
							request.Abort ();

						OnOpenReadCompleted (new OpenReadCompletedEventArgs (null, null, true, args[1]));
						}
#if SSHARP
					catch (ThreadAbortedException)
						{
						if (request != null)
							request.Abort ();

						ThreadPool.QueueUserWorkItem (_ => OnOpenReadCompleted (new OpenReadCompletedEventArgs (null, null, true, args[1])));
						}
#endif
					catch (Exception e)
						{
						OnOpenReadCompleted (new OpenReadCompletedEventArgs (null, e, false, args[1]));
						}
					});
				async_thread.IsBackground = true;
				async_thread.Start (cb_args);
#else
				ThreadPool.QueueUserWorkItem ((state) =>
					{
					object[] args = (object[])state;
					WebRequest request = null;
					try
						{
						request = SetupRequest ((Uri)args[0]);
						WebResponse response = GetWebResponse (request);
						Stream stream = response.GetResponseStream ();
						OnOpenReadCompleted (new OpenReadCompletedEventArgs (stream, null, false, args[1]));
						}
					catch (Exception e)
						{
						OnOpenReadCompleted (new OpenReadCompletedEventArgs (null, e, false, args[1]));
						}
					
					}, cb_args);
#endif
				}
			}

		//    OpenWriteAsync

		public void OpenWriteAsync (Uri address)
			{
			OpenWriteAsync (address, null);
			}

		public void OpenWriteAsync (Uri address, string method)
			{
			OpenWriteAsync (address, method, null);
			}

		public void OpenWriteAsync (Uri address, string method, object userToken)
			{
			if (address == null)
				throw new ArgumentNullException ("address");

			lock (this)
				{
				SetBusy ();
				async = true;

				object[] cb_args = new object[] {CreateUri (address), method, userToken};
#if THREADING
				async_thread = new Thread (delegate (object state)
					{
					object[] args = (object[])state;
					WebRequest request = null;
					try
						{
						request = SetupRequest ((Uri)args[0], (string)args[1], true);
						var stream = OpenWriteStream (request);
						OnOpenWriteCompleted (new OpenWriteCompletedEventArgs (stream, null, false, args[2]));
						}
					catch (ThreadInterruptedException)
						{
						if (request != null)
							request.Abort ();

						OnOpenWriteCompleted (new OpenWriteCompletedEventArgs (null, null, true, args[2]));
						}
#if SSHARP
					catch (ThreadAbortedException)
						{
						if (request != null)
							request.Abort ();

						ThreadPool.QueueUserWorkItem (_ => OnOpenWriteCompleted (new OpenWriteCompletedEventArgs (null, null, true, args[2])));
						}
#endif
					catch (Exception e)
						{
						OnOpenWriteCompleted (new OpenWriteCompletedEventArgs (null, e, false, args[2]));
						}
					});
				async_thread.IsBackground = true;
				async_thread.Start (cb_args);
#else
				ThreadPool.QueueUserWorkItem ((state) =>
					{
					object[] args = (object[])state;
					WebRequest request = null;
					try
						{
						request = SetupRequest ((Uri)args[0], (string)args[1], true);
						var stream = OpenWriteStream (request);
						OnOpenWriteCompleted (new OpenWriteCompletedEventArgs (stream, null, false, args[2]));
						}
					catch (Exception e)
						{
						OnOpenWriteCompleted (new OpenWriteCompletedEventArgs (null, e, false, args[2]));
						}
					
					}, cb_args);
#endif
				}
			}

		//    UploadDataAsync

		public void UploadDataAsync (Uri address, byte[] data)
			{
			UploadDataAsync (address, null, data);
			}

		public void UploadDataAsync (Uri address, string method, byte[] data)
			{
			UploadDataAsync (address, method, data, null);
			}

		public void UploadDataAsync (Uri address, string method, byte[] data, object userToken)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (data == null)
				throw new ArgumentNullException ("data");

			lock (this)
				{
				SetBusy ();
				async = true;

				object[] cb_args = new object[] {CreateUri (address), method, data, userToken};
#if THREADING
				async_thread = new Thread (delegate (object state)
					{
					object[] args = (object[])state;
					byte[] data2;

					try
						{
						data2 = UploadDataCore ((Uri)args[0], (string)args[1], (byte[])args[2], args[3]);

						OnUploadDataCompleted (new UploadDataCompletedEventArgs (data2, null, false, args[3]));
						}
					catch (ThreadInterruptedException)
						{
						OnUploadDataCompleted (new UploadDataCompletedEventArgs (null, null, true, args[3]));
						}
#if SSHARP
					catch (ThreadAbortedException)
						{
						ThreadPool.QueueUserWorkItem (_ => OnUploadDataCompleted (new UploadDataCompletedEventArgs (null, null, true, args[3])));
						}
#endif
					catch (Exception e)
						{
						OnUploadDataCompleted (new UploadDataCompletedEventArgs (null, e, false, args[3]));
						}
					});
				async_thread.IsBackground = true;
				async_thread.Start (cb_args);
#else				
				ThreadPool.QueueUserWorkItem ((state) =>
					{
					object[] args = (object[])state;
					byte[] data2;

					try
						{
						data2 = UploadDataCore ((Uri)args[0], (string)args[1], (byte[])args[2], args[3]);

						OnUploadDataCompleted (new UploadDataCompletedEventArgs (data2, null, false, args[3]));
						}
					catch (Exception e)
						{
						OnUploadDataCompleted (new UploadDataCompletedEventArgs (null, e, false, args[3]));
						}
					
					}, cb_args);
#endif
				}
			}

		//    UploadFileAsync

		public void UploadFileAsync (Uri address, string fileName)
			{
			UploadFileAsync (address, null, fileName);
			}

		public void UploadFileAsync (Uri address, string method, string fileName)
			{
			UploadFileAsync (address, method, fileName, null);
			}

		public void UploadFileAsync (Uri address, string method, string fileName, object userToken)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (fileName == null)
				throw new ArgumentNullException ("fileName");

			lock (this)
				{
				SetBusy ();
				async = true;

				object[] cb_args = new object[] {CreateUri (address), method, fileName, userToken};
#if THREADING
				async_thread = new Thread (delegate (object state)
					{
					object[] args = (object[])state;
					byte[] data;

					try
						{
						data = UploadFileCore ((Uri)args[0], (string)args[1], (string)args[2], args[3]);
						OnUploadFileCompleted (new UploadFileCompletedEventArgs (data, null, false, args[3]));
						}
					catch (ThreadInterruptedException)
						{
						OnUploadFileCompleted (new UploadFileCompletedEventArgs (null, null, true, args[3]));
						}
#if SSHARP
					catch (ThreadAbortedException)
						{
						ThreadPool.QueueUserWorkItem (_ => OnUploadFileCompleted (new UploadFileCompletedEventArgs (null, null, true, args[3])));
						}
#endif
					catch (Exception e)
						{
						OnUploadFileCompleted (new UploadFileCompletedEventArgs (null, e, false, args[3]));
						}
					});
				async_thread.IsBackground = true;
				async_thread.Start (cb_args);
#else
				ThreadPool.QueueUserWorkItem ((state) =>
					{
					object[] args = (object[])state;
					byte[] data;

					try
						{
						data = UploadFileCore ((Uri)args[0], (string)args[1], (string)args[2], args[3]);
						OnUploadFileCompleted (new UploadFileCompletedEventArgs (data, null, false, args[3]));
						}
					catch (Exception e)
						{
						OnUploadFileCompleted (new UploadFileCompletedEventArgs (null, e, false, args[3]));
						}
					}, cb_args);
#endif
				}
			}

		//    UploadStringAsync

		public void UploadStringAsync (Uri address, string data)
			{
			UploadStringAsync (address, null, data);
			}

		public void UploadStringAsync (Uri address, string method, string data)
			{
			UploadStringAsync (address, method, data, null);
			}

		public void UploadStringAsync (Uri address, string method, string data, object userToken)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (data == null)
				throw new ArgumentNullException ("data");

			lock (this)
				{
				CheckBusy ();
				async = true;

				object[] cb_args = new object[] {CreateUri (address), method, data, userToken};
#if THREADING
				async_thread = new Thread (delegate (object state)
					{
					object[] args = (object[])state;

					try
						{
						string data2 = UploadString ((Uri)args[0], (string)args[1], (string)args[2]);
						OnUploadStringCompleted (new UploadStringCompletedEventArgs (data2, null, false, args[3]));
						}
					catch (Exception e)
						{
						if (e is ThreadInterruptedException || e.InnerException is ThreadInterruptedException)
							{
							OnUploadStringCompleted (new UploadStringCompletedEventArgs (null, null, true, args[3]));
							return;
							}
#if SSHARP
						if (e is ThreadAbortedException || e.InnerException is ThreadAbortedException)
							{
							ThreadPool.QueueUserWorkItem (_ => OnUploadStringCompleted (new UploadStringCompletedEventArgs (null, null, true, args[3])));
							return;
							}
#endif
						OnUploadStringCompleted (new UploadStringCompletedEventArgs (null, e, false, args[3]));
						}
					});
				async_thread.IsBackground = true;
				async_thread.Start (cb_args);
#else
				ThreadPool.QueueUserWorkItem ((state) =>
					{
					object[] args = (object[])state;

					try
						{
						string data2 = UploadString ((Uri)args[0], (string)args[1], (string)args[2]);
						OnUploadStringCompleted (new UploadStringCompletedEventArgs (data2, null, false, args[3]));
						}
					catch (Exception e)
						{
						OnUploadStringCompleted (new UploadStringCompletedEventArgs (null, e, false, args[3]));
						}
					}, cb_args);
#endif
				}
			}

		//    UploadValuesAsync

		public void UploadValuesAsync (Uri address, NameValueCollection data)
			{
			UploadValuesAsync (address, null, data);
			}

		public void UploadValuesAsync (Uri address, string method, NameValueCollection data)
			{
			UploadValuesAsync (address, method, data, null);
			}

		public void UploadValuesAsync (Uri address, string method, NameValueCollection data, object userToken)
			{
			if (address == null)
				throw new ArgumentNullException ("address");
			if (data == null)
				throw new ArgumentNullException ("data");

			lock (this)
				{
				CheckBusy ();
				async = true;

				object[] cb_args = new object[] {CreateUri (address), method, data, userToken};
#if THREADING
				async_thread = new Thread (delegate (object state)
					{
					object[] args = (object[])state;
					try
						{
						byte[] values = UploadValuesCore ((Uri)args[0], (string)args[1], (NameValueCollection)args[2], args[3]);
						OnUploadValuesCompleted (new UploadValuesCompletedEventArgs (values, null, false, args[3]));
						}
					catch (ThreadInterruptedException)
						{
						OnUploadValuesCompleted (new UploadValuesCompletedEventArgs (null, null, true, args[3]));
						}
#if SSHARP
					catch (ThreadAbortedException)
						{
						ThreadPool.QueueUserWorkItem (_ => OnUploadValuesCompleted (new UploadValuesCompletedEventArgs (null, null, true, args[3])));
						}
#endif
					catch (Exception e)
						{
						OnUploadValuesCompleted (new UploadValuesCompletedEventArgs (null, e, false, args[3]));
						}
					});
				async_thread.IsBackground = true;
				async_thread.Start (cb_args);
#else
				ThreadPool.QueueUserWorkItem ((state) =>
					{
					object[] args = (object[])state;
					try
						{
						byte[] values = UploadValuesCore ((Uri)args[0], (string)args[1], (NameValueCollection)args[2], args[3]);
						OnUploadValuesCompleted (new UploadValuesCompletedEventArgs (values, null, false, args[3]));
						}
					catch (Exception e)
						{
						OnUploadValuesCompleted (new UploadValuesCompletedEventArgs (null, e, false, args[3]));
						}
					}, cb_args);
#endif
				}
			}

		protected virtual void OnDownloadDataCompleted (DownloadDataCompletedEventArgs e)
			{
			CompleteAsync ();
			if (DownloadDataCompleted != null)
				DownloadDataCompleted (this, e);
			}

		protected virtual void OnDownloadFileCompleted (AsyncCompletedEventArgs e)
			{
			CompleteAsync ();
			if (DownloadFileCompleted != null)
				DownloadFileCompleted (this, e);
			}

		protected virtual void OnDownloadProgressChanged (DownloadProgressChangedEventArgs e)
			{
			if (DownloadProgressChanged != null)
				DownloadProgressChanged (this, e);
			}

		protected virtual void OnDownloadStringCompleted (DownloadStringCompletedEventArgs e)
			{
			CompleteAsync ();
			if (DownloadStringCompleted != null)
				DownloadStringCompleted (this, e);
			}

		protected virtual void OnOpenReadCompleted (OpenReadCompletedEventArgs e)
			{
			CompleteAsync ();
			if (OpenReadCompleted != null)
				OpenReadCompleted (this, e);
			}

		protected virtual void OnOpenWriteCompleted (OpenWriteCompletedEventArgs e)
			{
			CompleteAsync ();
			if (OpenWriteCompleted != null)
				OpenWriteCompleted (this, e);
			}

		protected virtual void OnUploadDataCompleted (UploadDataCompletedEventArgs e)
			{
			CompleteAsync ();
			if (UploadDataCompleted != null)
				UploadDataCompleted (this, e);
			}

		protected virtual void OnUploadFileCompleted (UploadFileCompletedEventArgs e)
			{
			CompleteAsync ();
			if (UploadFileCompleted != null)
				UploadFileCompleted (this, e);
			}

		protected virtual void OnUploadProgressChanged (UploadProgressChangedEventArgs e)
			{
			if (UploadProgressChanged != null)
				UploadProgressChanged (this, e);
			}

		protected virtual void OnUploadStringCompleted (UploadStringCompletedEventArgs e)
			{
			CompleteAsync ();
			if (UploadStringCompleted != null)
				UploadStringCompleted (this, e);
			}

		protected virtual void OnUploadValuesCompleted (UploadValuesCompletedEventArgs e)
			{
			CompleteAsync ();
			if (UploadValuesCompleted != null)
				UploadValuesCompleted (this, e);
			}

		protected virtual WebResponse GetWebResponse (WebRequest request, IAsyncResult result)
			{
			WebResponse response = request.EndGetResponse (result);
			responseHeaders = response.Headers;
			return response;
			}

		protected virtual WebRequest GetWebRequest (Uri address)
			{
			return WebRequest.Create (address);
			}

		protected virtual WebResponse GetWebResponse (WebRequest request)
			{
			WebResponse response = request.GetResponse ();
			responseHeaders = response.Headers;
			return response;
			}
		}
	}
