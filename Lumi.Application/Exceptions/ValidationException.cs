using System;

namespace Lumi.Application.Exceptions
{
    // Class này dùng để ném ra các lỗi do nghiệp vụ/người dùng nhập sai
    public class ValidationException : Exception
    {
        public ValidationException(string message) : base(message) { }
    }
}