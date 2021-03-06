﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace FOnline
{
    // internal common functionality
    public static class CoreUtils
    {
        public static string ParseFuncName(string func_name)
        {
            // if name contains ::, we're fine
            if (func_name.Contains(':'))
                return "mono@" + func_name;
            else // otherwise, we need to fetch parenting type and prepare full name
            {
                var stack = new StackFrame(2); // a little dirty hack, performance alert
                var type = stack.GetMethod().DeclaringType;
                return "mono@" + type.FullName;
            }
        }
        public static string ParseFuncName(Delegate d)
        {
            var method = d.Method;
            return ParseFuncName(method.DeclaringType.FullName + "::" + method.Name);
        }
    }
}
