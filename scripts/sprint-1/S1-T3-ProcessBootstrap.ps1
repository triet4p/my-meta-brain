# Windows PowerShell 5.1, x64. This uses Reflection.Emit P/Invoke metadata rather than Add-Type,
# so a kill-on-close job can contain the current PowerShell before any compiler/helper launches.
function New-S1T3BootstrapProcessJob {
    if (-not [Environment]::Is64BitProcess) { throw 'The process-safety bootstrap requires 64-bit PowerShell.' }

    $assemblyName = [Reflection.AssemblyName]::new('S1T3Bootstrap-' + [Guid]::NewGuid().ToString('N'))
    $assembly = [AppDomain]::CurrentDomain.DefineDynamicAssembly($assemblyName, [Reflection.Emit.AssemblyBuilderAccess]::Run)
    $module = $assembly.DefineDynamicModule('S1T3Bootstrap')
    $typeBuilder = $module.DefineType('S1T3BootstrapNative', [Reflection.TypeAttributes]::Public)
    $methodAttributes = [Reflection.MethodAttributes]::Public -bor [Reflection.MethodAttributes]::Static -bor [Reflection.MethodAttributes]::PinvokeImpl
    $callingConvention = [Reflection.CallingConventions]::Standard
    $nativeConvention = [Runtime.InteropServices.CallingConvention]::Winapi
    $unicode = [Runtime.InteropServices.CharSet]::Unicode

    $createJob = $typeBuilder.DefinePInvokeMethod('CreateJobObjectW', 'kernel32.dll', 'CreateJobObjectW',
        $methodAttributes, $callingConvention, [IntPtr], [Type[]]@([IntPtr], [string]), $nativeConvention, $unicode)
    $setJobInfo = $typeBuilder.DefinePInvokeMethod('SetInformationJobObject', 'kernel32.dll', 'SetInformationJobObject',
        $methodAttributes, $callingConvention, [bool], [Type[]]@([IntPtr], [int], [IntPtr], [uint32]), $nativeConvention, $unicode)
    $assignProcess = $typeBuilder.DefinePInvokeMethod('AssignProcessToJobObject', 'kernel32.dll', 'AssignProcessToJobObject',
        $methodAttributes, $callingConvention, [bool], [Type[]]@([IntPtr], [IntPtr]), $nativeConvention, $unicode)
    $getCurrentProcess = $typeBuilder.DefinePInvokeMethod('GetCurrentProcess', 'kernel32.dll', 'GetCurrentProcess',
        $methodAttributes, $callingConvention, [IntPtr], [Type[]]@(), $nativeConvention, $unicode)
    $closeHandle = $typeBuilder.DefinePInvokeMethod('CloseHandle', 'kernel32.dll', 'CloseHandle',
        $methodAttributes, $callingConvention, [bool], [Type[]]@([IntPtr]), $nativeConvention, $unicode)
    foreach ($method in @($createJob, $setJobInfo, $assignProcess, $getCurrentProcess, $closeHandle)) {
        $method.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
    }
    $native = $typeBuilder.CreateType()

    $job = [IntPtr]::Zero
    $limits = [IntPtr]::Zero
    try {
        $job = [IntPtr]$native.GetMethod('CreateJobObjectW').Invoke($null, [object[]]@([IntPtr]::Zero, $null))
        if ($job -eq [IntPtr]::Zero) { throw 'CreateJobObjectW failed before launcher initialization.' }

        # JOBOBJECT_EXTENDED_LIMIT_INFORMATION is 144 bytes on x64; LimitFlags is
        # the DWORD at byte offset 16 in its BASIC_LIMIT_INFORMATION member.
        $limitInformationSize = 144
        $limits = [Runtime.InteropServices.Marshal]::AllocHGlobal($limitInformationSize)
        for ($offset = 0; $offset -lt $limitInformationSize; $offset += 4) {
            [Runtime.InteropServices.Marshal]::WriteInt32($limits, $offset, 0)
        }
        [Runtime.InteropServices.Marshal]::WriteInt32($limits, 16, 0x2000)
        $configured = $native.GetMethod('SetInformationJobObject').Invoke(
            $null, [object[]]@($job, 9, $limits, [uint32]$limitInformationSize))
        if (-not [bool]$configured) { throw 'SetInformationJobObject(KILL_ON_JOB_CLOSE) failed before launcher initialization.' }

        $currentProcess = [IntPtr]$native.GetMethod('GetCurrentProcess').Invoke($null, [object[]]@())
        $assigned = $native.GetMethod('AssignProcessToJobObject').Invoke($null, [object[]]@($job, $currentProcess))
        if (-not [bool]$assigned) { throw 'AssignProcessToJobObject(current PowerShell) failed before launcher initialization.' }
        $ownedJob = $job
        $job = [IntPtr]::Zero
        return $ownedJob
    }
    finally {
        if ($limits -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::FreeHGlobal($limits) }
        if ($job -ne [IntPtr]::Zero) { $null = $native.GetMethod('CloseHandle').Invoke($null, [object[]]@($job)) }
    }
}
