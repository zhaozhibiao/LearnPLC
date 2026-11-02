# SPDX-License-Identifier: MIT
"""Read-only fixed query through the RuntimeManager's existing IPC owner."""
def task_scheduling(manager):
    supported=False
    reply=None
    try:
        response=manager.runtime_socket.send_and_receive('DEBUG:46 02\n',timeout=2.0)
        if isinstance(response,str) and len(response)<=64:reply=response.strip()
        if isinstance(response,str) and response.startswith('DEBUG:') and len(response)<=64:
            supported=bytes.fromhex(response[6:].strip())==bytes([0x46,0x7e,1])
    except (OSError,ValueError,TimeoutError):pass
    return dict(interface='workbench-task-scheduling-v1',supported=supported,nativeReply=reply)
