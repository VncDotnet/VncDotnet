using System;
using System.Collections.Generic;
using System.Text;

namespace VncDotnet
{
    public class VncException : Exception
    {

    }

    public class VncConnectionException : VncException
    {

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
