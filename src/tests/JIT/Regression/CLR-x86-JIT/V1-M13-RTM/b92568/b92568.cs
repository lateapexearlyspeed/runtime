// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//

using System;
public struct AA
{
    static bool Static3(ulong param2)
    {
        bool b = false;
        return (bool)(object)(long)(byte)(b ? Convert.ToInt64(param2) : (long)param2);
    }
    public static int Main()
    {
        try
        {
            Static3(0);
            return 101;
        }
        catch (InvalidCastException)
        {
            return 100;
        }
    }
}
