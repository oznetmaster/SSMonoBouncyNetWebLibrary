//
// System.Net.FileWebResponse
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
using System.Globalization;
#if SSHARP
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronIO;
//using FileStream = SSMono.IO.FileStream;
using GC = Crestron.SimplSharp.CrestronEnvironment.GC;
#else
using System.IO;
using System.Runtime.Serialization;
#endif

#if SSHARP
namespace SSMono.Net
#else
namespace System.Net
#endif
	{
	[Serializable]
	public class FileWebResponse : WebResponse,
#if !NETCF
		ISerializable,
#endif
		IDisposable
		{
		private Uri m_uri;
		private Stream m_stream;
		private long m_contentLength;
		private FileAccess m_fileAccess;
		private WebHeaderCollection webHeaders;
		private bool disposed;
		private Exception exception;

		// Constructors

		internal FileWebResponse (Uri responseUri, FileStream fileStream)
			{
			try
				{
				this.m_uri = responseUri;
				this.m_stream = fileStream;
				this.m_contentLength = fileStream.Length;
				this.webHeaders = new WebHeaderCollection ();
				this.webHeaders.Add ("Content-Length", Convert.ToString (m_contentLength));
				this.webHeaders.Add ("Content-Type", "application/octet-stream");
				}
			catch (Exception e)
				{
				throw new WebException (e.Message, e);
				}
			}

		internal FileWebResponse (FileWebRequest request, Uri uri, FileAccess access, bool asyncHint)
			{
			try
				{
				m_fileAccess = access;
				if (access == FileAccess.Write)
					{
					m_stream = Stream.Null;
					}
				else
					{

					//
					// apparently, specifying async when the stream will be read
					// synchronously, or vice versa, can lead to a 10x perf hit.
					// While we don't know how the app will read the stream, we
					// use the hint from whether the app called BeginGetResponse
					// or GetResponse to supply the async flag to the stream ctor
					//

					m_stream = new FileWebRequest.FileWebStream (request,
														  uri.LocalPath,
														  FileMode.Open,
														  FileAccess.Read,
														  FileShare.Read
														  );
					m_contentLength = m_stream.Length;
					}
				this.webHeaders = new WebHeaderCollection ();
				this.webHeaders.Add ("Content-Length", Convert.ToString (m_contentLength));
				this.webHeaders.Add ("Content-Type", "application/octet-stream");
				m_uri = uri;
				}
			catch (Exception e)
				{
				throw new WebException (e.Message, e, WebExceptionStatus.ConnectFailure, null);
				}
			}

		internal FileWebResponse (Uri responseUri, WebException exception)
			{
			this.m_uri = responseUri;
			this.exception = exception;
			}

#if !NETCF
		[Obsolete ("Serialization is obsoleted for this type", false)]
		protected FileWebResponse (SerializationInfo serializationInfo, StreamingContext streamingContext)
			{
			SerializationInfo info = serializationInfo;

			responseUri = (Uri)info.GetValue ("responseUri", typeof(Uri));
			contentLength = info.GetInt64 ("contentLength");
			webHeaders = (WebHeaderCollection)info.GetValue ("webHeaders", typeof(WebHeaderCollection));
			}
#endif

		// Properties
		internal bool HasError
			{
			get { return exception != null; }
			}

		internal Exception Error
			{
			get { return exception; }
			}

		public override long ContentLength
			{
			get
				{
				CheckDisposed ();
				return this.m_contentLength;
				}
			}

		public override string ContentType
			{
			get
				{
				CheckDisposed ();
				return "application/octet-stream";
				}
			}

		public override WebHeaderCollection Headers
			{
			get
				{
				CheckDisposed ();
				return this.webHeaders;
				}
			}

		public override Uri ResponseUri
			{
			get
				{
				CheckDisposed ();
				return this.m_uri;
				}
			}

		// Methods

#if !NETCF
		void ISerializable.GetObjectData (SerializationInfo serializationInfo, StreamingContext streamingContext)
			{
			GetObjectData (serializationInfo, streamingContext);
			}

		protected override void GetObjectData (SerializationInfo serializationInfo, StreamingContext streamingContext)
			{
			SerializationInfo info = serializationInfo;

			info.AddValue ("responseUri", responseUri, typeof(Uri));
			info.AddValue ("contentLength", contentLength);
			info.AddValue ("webHeaders", webHeaders, typeof(WebHeaderCollection));
			}
#endif

		public override Stream GetResponseStream ()
			{
			CheckDisposed ();
			return this.m_stream;
			}

		// Cleaning up stuff

		~FileWebResponse ()
			{
			Dispose (false);
			}

		public override void Close ()
			{
			((IDisposable)this).Dispose ();
			}

		void IDisposable.Dispose ()
			{
			Dispose (true);

			// see spec, suppress finalization of this object.
			GC.SuppressFinalize (this);
			}

#if NET_4_0
		protected override
#else
		private
#endif
			void Dispose (bool disposing)
			{
			if (this.disposed)
				return;
			this.disposed = true;

			if (disposing)
				{
				// release managed resources
				this.m_uri = null;
				this.webHeaders = null;

				Stream stream = m_stream;
				m_stream = null;
				if (stream != null)
					stream.Close (); // also closes webRequest
				}

			// release unmanaged resources
#if NET_4_0
			base.Dispose (disposing);
#endif
			}

		private void CheckDisposed ()
			{
			if (disposed)
				throw new ObjectDisposedException (GetType ().FullName);
			}
		}
	}