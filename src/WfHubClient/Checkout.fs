namespace WfHubClient

module Checkout =
    let checkout (host: string, projectId: int, token: string, gitRef: string option) : System.Threading.Tasks.Task<obj> =
        raise (System.NotImplementedException "checkout is not implemented yet")
