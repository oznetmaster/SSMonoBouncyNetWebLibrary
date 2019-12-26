//
// System.Net.FileWebRequest
//
// Author:
//   Lawrence Pit (loz@cable.a2000.nl)
//

// Copyright (c) 2018 Nivloc Enterprises Ltd

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

using System;
using System.Collections;
using SSMono.Threading;
#if SSHARP
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronIO;
using IAsyncResult = Crestron.SimplSharp.CrestronIO.IAsyncResult;
using AsyncCallback = Crestron.SimplSharp.CrestronIO.AsyncCallback;

//using FileStream = SSMono.IO.FileStream;
#else
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Remoting.Messaging;
using System.Threading;
#endif

#if SSHARP

namespace SSMono.Net
#else
namespace System.Net
#endif
	{
	[Serializable]
	public class FileWebRequest : WebRequest
#if !NETCF
		, ISerializable
#endif
		{
		private Uri m_uri;
		private WebHeaderCollection m_headers;

		private ICredentials m_credentials;
		private FileAccess m_fileAccess;
		private string m_connectionGroupName;
		private long m_contentLength;
#if !NETCF
		private FileAccess fileAccess = FileAccess.Read;
#endif
		private string m_method = "GET";
		private IWebProxy m_proxy;
		private ManualResetEvent m_readerEvent;
		private bool m_preauthenticate;
		private int m_timeout = 100000;

		private Stream m_stream;
		private FileWebResponse m_response;
		private AutoResetEvent requestEndEvent;
		private bool m_readPending;
		private bool m_writePending;
		private bool m_writing;

		private LazyAsyncResult m_WriteAResult;
		private LazyAsyncResult m_ReadAResult;
		private int m_Aborted;

		// Constructors

		internal FileWebRequest (Uri uri)
			{
			this.m_uri = uri;
			m_headers = new WebHeaderCollection ();
			}

#if !NETCF
		[Obsolete ("Serialization is obsoleted for this type", false)]
		protected FileWebRequest (SerializationInfo serializationInfo, StreamingContext streamingContext)
			{
			SerializationInfo info = serializationInfo;
			webHeaders = (WebHeaderCollection)info.GetValue ("headers", typeof(WebHeaderCollection));
			proxy = (IWebProxy)info.GetValue ("proxy", typeof(IWebProxy));
			uri = (Uri)info.GetValue ("uri", typeof(Uri));
			connectionGroup = info.GetString ("connectionGroupName");
			method = info.GetString ("method");
			contentLength = info.GetInt64 ("contentLength");
			timeout = info.GetInt32 ("timeout");
			fileAccess = (FileAccess)info.GetValue ("fileAccess", typeof(FileAccess));
			preAuthenticate = info.GetBoolean ("preauthenticate");
			}
#endif

		// Properties

		internal bool Aborted
			{
			get { return m_Aborted != 0; }
			}

		// currently not used according to spec
		public override string ConnectionGroupName
			{
			get { return m_connectionGroupName; }
			set { m_connectionGroupName = value; }
			}

		public override long ContentLength
			{
			get { return m_contentLength; }
			set
				{
				if (value < 0)
					throw new ArgumentException ("The Content-Length value must be greater than or equal to zero.", "value");
				m_contentLength = value;
				}
			}

		public override string ContentType
			{
			get { return m_headers["Content-Type"]; }
			set { m_headers["Content-Type"] = value; }
			}

		public override ICredentials Credentials
			{
			get { return m_credentials; }
			set { m_credentials = value; }
			}

		public override WebHeaderCollection Headers
			{
			get { return m_headers; }
			}

		// currently not used according to spec
		public override string Method
			{
			get { return m_method; }
			set
				{
				if (value == null || value.Length == 0)
					throw new ArgumentException ("Cannot set null or blank " + "methods on request.", "value");
				m_method = value;
				}
			}

		// currently not used according to spec
		public override bool PreAuthenticate
			{
			get { return m_preauthenticate; }
			set { m_preauthenticate = value; }
			}

		// currently not used according to spec
		public override IWebProxy Proxy
			{
			get { return m_proxy; }
			set { m_proxy = value; }
			}

		public override Uri RequestUri
			{
			get { return m_uri; }
			}

		public override int Timeout
			{
			get { return m_timeout; }
			set
				{
				if (value < -1)
					throw new ArgumentOutOfRangeException ("Timeout can be " + "only set to 'System.Threading.Timeout.Infinite' " + "or a value >= 0.");
				m_timeout = value;
				}
			}

		public override bool UseDefaultCredentials
			{
			get { throw new NotSupportedException (); }
			set { throw new NotSupportedException (); }
			}

		// Methods

		private delegate void GetRequestStreamCallback (object state);

		private delegate WebResponse GetResponseCallback ();

		private static Exception GetMustImplement ()
			{
			return new NotImplementedException ();
			}

		/* LAMESPEC: Docs suggest this was present in 1.1 and
		 * 1.0 profiles, but the masterinfos say otherwise
		 */

		public override void Abort ()
			{
			if (Interlocked.Increment (ref m_Aborted) == 1)
				{
				LazyAsyncResult readAResult = m_ReadAResult;
				LazyAsyncResult writeAResult = m_WriteAResult;

				WebException webException = new WebException ("abort requested", WebExceptionStatus.RequestCanceled);

				Stream requestStream = m_stream;

				if (readAResult != null && !readAResult.IsCompleted)
					readAResult.InvokeCallback (webException);
				if (writeAResult != null && !writeAResult.IsCompleted)
					writeAResult.InvokeCallback (webException);

				if (requestStream != null)
					requestStream.Close ();

				if (m_response != null)
					m_response.Close ();
				}
			}

		public override IAsyncResult BeginGetRequestStream (AsyncCallback callback, object state)
			{
			if (Aborted)
				throw new WebException ("request aborted", WebExceptionStatus.RequestCanceled);

			if (string.Compare ("GET", m_method, true) == 0 || string.Compare ("HEAD", m_method, true) == 0 || string.Compare ("CONNECT", m_method, true) == 0)
				throw new ProtocolViolationException ("Cannot send a content-body with this verb-type.");

			lock (this)
				{
				if (m_response != null)
					throw new InvalidOperationException ("This operation cannot be performed after the request has been submitted.");
				if (m_writePending)
					throw new InvalidOperationException ("Cannot re-call start of asynchronous method while a previous call is still in progress.");
				m_writePending = true;
				}

			m_ReadAResult = new LazyAsyncResult (this, state, callback);
			ThreadPool.QueueUserWorkItem (GetRequestStreamInternal, m_ReadAResult);
			return m_ReadAResult;
			}

		public override Stream EndGetRequestStream (IAsyncResult asyncResult)
			{
			Stream stream;

			LazyAsyncResult ar = asyncResult as LazyAsyncResult;
			if (asyncResult == null || ar == null)
				{
				Exception e = asyncResult == null ? new ArgumentNullException ("asyncResult") : new ArgumentException ("invalid iasyncresult", "asyncResult");
				throw e;
				}

			object result = ar.InternalWaitForCompletion ();
			var exception = result as Exception;
			if (exception != null)
				{
				throw exception;
				}

			stream = (Stream)result;
			m_writePending = false;

			return stream;
			}

		public override Stream GetRequestStream ()
			{
			IAsyncResult result = BeginGetRequestStream (null, null);

			if ((Timeout != Crestron.SimplSharp.Timeout.Infinite) && !result.IsCompleted)
				{
				if (!result.AsyncWaitHandle.WaitOne (Timeout, false) || !result.IsCompleted)
					{
					if (m_stream != null)
						{
						m_stream.Close ();
						}
					throw new WebException ("request timed out", WebExceptionStatus.Timeout);
					}
				}

			return EndGetRequestStream (result);
			}

		internal static void GetRequestStreamInternal (object state)
			{
			LazyAsyncResult asyncResult = (LazyAsyncResult)state;
			FileWebRequest request = (FileWebRequest)asyncResult.AsyncObject;
			try
				{
				if (request.m_stream == null)
					{
					request.m_stream = new FileWebStream (request, request.m_uri.LocalPath, FileMode.Create, FileAccess.Write, FileShare.Read);
					request.m_fileAccess = FileAccess.Write;
					request.m_writing = true;
					}
				}
			catch (Exception ex)
				{
				ex = new WebException (ex.Message, ex);
				// if the callback throws, correct behavior is to crash the process
				asyncResult.InvokeCallback (ex);
				return;
				}
			// if the callback throws, correct behavior is to crash the process
			asyncResult.InvokeCallback (request.m_stream);
			}

		public override IAsyncResult BeginGetResponse (AsyncCallback callback, object state)
			{
			if (Aborted)
				throw new WebException ("request aborted", WebExceptionStatus.RequestCanceled);

			lock (this)
				{
				if ( m_readPending)
					throw new InvalidOperationException ("Cannot re-call start of asynchronous method while a previous call is still in progress.");
				m_readPending = true;
				}

			m_WriteAResult = new LazyAsyncResult (this, state, callback);
			ThreadPool.QueueUserWorkItem (GetResponseInternal, m_WriteAResult);

			return m_WriteAResult;
			}

		public override WebResponse EndGetResponse (IAsyncResult asyncResult)
			{
			var ar = asyncResult as LazyAsyncResult;
			if (asyncResult == null || ar == null)
				{
				Exception e = asyncResult == null ? new ArgumentNullException ("asyncResult") : new ArgumentException ("invalid iasyncresult", "asyncResult");
				throw e;
				}

			object result = ar.InternalWaitForCompletion ();
			var exception = result as Exception;
			if (exception != null)
				{
				throw exception;
				}

			var response = (WebResponse)result;
			m_readPending = false;

			return response;
			}

		public override WebResponse GetResponse ()
			{
			IAsyncResult result = BeginGetResponse (null, null);

			if ((Timeout != Crestron.SimplSharp.Timeout.Infinite) && !result.IsCompleted)
				{
				if (!result.AsyncWaitHandle.WaitOne (Timeout, false) || !result.IsCompleted)
					{
					if (m_response != null)
						{
						m_response.Close ();
						}
					throw new WebException ("response timed out", WebExceptionStatus.Timeout);
					}
				}

			return EndGetResponse (result);
			}

		private static void GetResponseInternal (object state)
			{
			LazyAsyncResult asyncResult = (LazyAsyncResult)state;
			FileWebRequest request = (FileWebRequest)asyncResult.AsyncObject;

			if (request.m_writePending || request.m_writing)
				{
				lock (request)
					{
					if (request.m_writePending || request.m_writing)
						{
						request.m_readerEvent = new ManualResetEvent (false);
						}
					}
				}
			if (request.m_readerEvent != null)
				request.m_readerEvent.WaitOne ();

			try
				{
				if (request.m_response == null)
					request.m_response = new FileWebResponse (request, request.m_uri, request.m_fileAccess,  /*!request.m_syncHint*/ false);
				}
			catch (Exception e)
				{
				// any exceptions previously thrown must be passed to the callback
				Exception ex = new WebException (e.Message, e);

				// if the callback throws, correct behavior is to crash the process
				asyncResult.InvokeCallback (ex);
				return;
				}

			// if the callback throws, the correct behavior is to crash the process
			asyncResult.InvokeCallback (request.m_response);
			}

#if !NETCF
		void ISerializable.GetObjectData (SerializationInfo serializationInfo, StreamingContext streamingContext)
			{
			GetObjectData (serializationInfo, streamingContext);
			}

		protected override void GetObjectData (SerializationInfo serializationInfo, StreamingContext streamingContext)
			{
			SerializationInfo info = serializationInfo;
			info.AddValue ("headers", webHeaders, typeof(WebHeaderCollection));
			info.AddValue ("proxy", proxy, typeof(IWebProxy));
			info.AddValue ("uri", uri, typeof(Uri));
			info.AddValue ("connectionGroupName", connectionGroup);
			info.AddValue ("method", method);
			info.AddValue ("contentLength", contentLength);
			info.AddValue ("timeout", timeout);
			info.AddValue ("fileAccess", fileAccess);
			info.AddValue ("preauthenticate", false);
			}
#endif

		internal void UnblockReader ()
			{
			lock (this)
				{
				if (m_readerEvent != null)
					{
					m_readerEvent.Set ();
					}
				}
			m_writing = false;
			}

		internal void Close ()
			{
			// already done in class below
			// if (requestStream != null) {
			// 	requestStream.Close ();
			// }

			lock (this)
				{
				m_writePending = false;
				if (requestEndEvent != null)
					requestEndEvent.Set ();
				// requestEndEvent = null;
				}
			}

		// to catch the Close called on the FileStream
		internal class FileWebStream : FileStream
			{
			private FileWebRequest m_request;

			internal FileWebStream (FileWebRequest webRequest, string path, FileMode mode, FileAccess access, FileShare share)
				: base (path, mode, access, share)
				{
				m_request = webRequest;
				}

			public override void Close ()
				{
				base.Close ();
				FileWebRequest req = m_request;
				m_request = null;
				if (req != null)
					req.Close ();
				}

			protected override void Dispose (bool disposing)
				{
				try
					{
					if (disposing && m_request != null)
						{
						m_request.UnblockReader ();
						}
					}
				finally
					{
					base.Dispose (disposing);
					}
				}

			public override int Read (byte[] buffer, int offset, int size)
				{
				CheckError ();
				try
					{
					return base.Read (buffer, offset, size);
					}
				catch
					{
					CheckError ();
					throw;
					}
				}

			public override void Write (byte[] buffer, int offset, int size)
				{
				CheckError ();
				try
					{
					base.Write (buffer, offset, size);
					}
				catch
					{
					CheckError ();
					throw;
					}
				}

			public override IAsyncResult BeginRead (byte[] buffer, int offset, int size, AsyncCallback callback, Object state)
				{
				CheckError ();
				try
					{
					return base.BeginRead (buffer, offset, size, callback, state);
					}
				catch
					{
					CheckError ();
					throw;
					}
				}

			public override int EndRead (IAsyncResult ar)
				{
				try
					{
					return base.EndRead (ar);
					}
				catch
					{
					CheckError ();
					throw;
					}
				}

			public override IAsyncResult BeginWrite (byte[] buffer, int offset, int size, AsyncCallback callback, Object state)
				{
				CheckError ();
				try
					{
					return base.BeginWrite (buffer, offset, size, callback, state);
					}
				catch
					{
					CheckError ();
					throw;
					}
				}

			public override void EndWrite (IAsyncResult ar)
				{
				try
					{
					base.EndWrite (ar);
					}
				catch
					{
					CheckError ();
					throw;
					}
				}

			private void CheckError ()
				{
				if (m_request.Aborted)
					{
					throw new WebException ("request aborted", WebExceptionStatus.RequestCanceled);
					}
				}
			}
		}
	}