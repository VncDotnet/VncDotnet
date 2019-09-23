using System;
using System.Collections.Generic;
using System.Text;

namespace VncDotnet
{
    public class VncException : Exception
    {
        public VncException() { }
        public VncException(string message) : base(message) { }
        public VncException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class VncConnectionException : VncException
    {
        public VncConnectionException() { }
        public VncConnectionException(string message) : base(message) { }
        public VncConnectionException(string message, Exception e) : base(message, e) { }
    }

    public class VncConnectException : VncException
    {

    }

    public class NoSecurityTypesException : VncConnectException
    {

    }

    public class RejectException : VncConnectException
    {

    }

    public class SecurityTypesMismatchException : VncConnectException
    {

    }

    public class ProtocolVersionMismatchException : VncConnectException
    {

    }
}
